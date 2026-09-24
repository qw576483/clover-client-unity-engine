using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace CloverEngine
{
    /// <summary>
    /// 会话通道加密（AES-256-GCM），与服务端 <c>internal/transport/net/session/crypto.go</c> 逐字节对齐。
    ///
    /// <para><b>线格式</b>：<c>[12B nonce][ciphertext || tag(16B)]</c>，整帧（含 8B 帧头）一起加密。</para>
    ///
    /// <para><b>密钥来源</b>：登录请求里声明 <c>ELoginRequest.encrypt = true</c>（由 NetworkManager 自动填），
    /// 服务端据此生成 32B 随机密钥，随登录回包 <c>ELoginReply.session_key</c>（base64，<b>明文</b>）下发；
    /// 网关随即对本连接的后续所有帧加解密。登录回包自身是明文的——客户端要靠它拿到密钥。</para>
    ///
    /// <para><b>平台可用性</b>：走 .NET 的 <see cref="AesGcm"/>。桌面 / 移动端可用；
    /// WebGL 等平台没有平台级实现（调用即抛 <see cref="PlatformNotSupportedException"/>），
    /// <see cref="IsSupported"/> 用一次自检来判定——不支持就**不声明** encrypt，服务端保持明文，
    /// 而不是"声明了却解不开"以致登录后所有帧失败。</para>
    /// </summary>
    internal static class SessionCrypto
    {
        /// <summary>密钥长度（AES-256）。与服务端 session.KeySize 一致。</summary>
        public const int KeySize = 32;

        /// <summary>GCM nonce 长度（标准 12 字节）。与服务端 session.NonceSize 一致。</summary>
        public const int NonceSize = 12;

        /// <summary>GCM 认证标签长度（字节）。</summary>
        public const int TagSize = 16;

        /// <summary>
        /// 服务端 <c>session.Decrypt</c> 接受的**密文**长度上限 = 服务端 <c>crypto.go:39-48</c>
        /// 的 <c>maxDecryptSize</c>；该常量 = <c>session.MaxFrameSize</c>
        /// （服务端帧上限的**唯一来源**，<c>crypto.go:26-37</c>：网关 <c>max_frame_size</c> 默认值、
        /// tcp/ws/quic 传输层上限、本上限全都引它）。**与帧上限同值 = 10 MiB**。
        /// 超过它的密文服务端一律判失败（<c>crypto.go:117-121</c> 返回 <c>ErrDecryptFailed</c>），
        /// 网关随即按「密钥不符 / 被篡改」**关闭连接**（<c>gwcore/session.go:497-503</c>）——
        /// 不只是丢这一帧。
        /// </summary>
        private const int MaxCiphertextBytes = 10 << 20; // = 服务端 session.MaxFrameSize

        /// <summary>
        /// 可加密的**明文**上限 = 服务端密文上限 − nonce − tag（= 10485732，与服务端同源同值）。
        /// 与服务端 <c>Encrypt</c> 的同名护栏逐值一致（<c>crypto.go:95-100</c>：明文超过
        /// <c>maxDecryptSize - NonceSize - Overhead</c> 即返回 <c>ErrPlaintextTooLarge</c>）。
        ///
        /// <para>
        /// 本类加密的是**整帧**（8B 帧头 + body），因此它同时是「启用通道加密后单帧的实际上限」：
        /// 与 <c>ClientFrame.MaxBodySize</c>（10 MiB，对应服务端闸门 <c>gateway.max_frame_size</c>）
        /// **同一个量级**，差别只有密文的 nonce + tag 28B（明文侧再加 8B 帧头）。
        /// </para>
        /// </summary>
        public const int MaxPlaintextBytes = MaxCiphertextBytes - NonceSize - TagSize;

        /// <summary>-1 = 未探测；0 = 不支持；1 = 支持。</summary>
        private static int _supported = -1;

        /// <summary>AesGcm 复用实例的访问门（静态缓存，加解密统一在锁内执行）。</summary>
        private static readonly object GcmGate = new object();

        /// <summary>复用中的 AesGcm 实例（热路径不按包 new + 导入密钥）。</summary>
        private static AesGcm _gcm;

        /// <summary>_gcm 对应的密钥数组引用（引用相等判定"该重建了"）。</summary>
        private static byte[] _gcmKey;

        /// <summary>
        /// 取/建复用中的 AesGcm 实例（**调用方必须已持有 <see cref="GcmGate"/>**）。
        /// 缓存键用引用相等：调用方（NetworkManager）对同一连接始终传同一个密钥数组实例，
        /// 轮换密钥 / 清空连接加密态会换新实例或置 null，引用比较可可靠判定重建。
        /// </summary>
        private static AesGcm GetGcmLocked(byte[] key)
        {
            if (_gcm == null || !ReferenceEquals(_gcmKey, key))
            {
                _gcm?.Dispose();
                _gcm = new AesGcm(key);
                _gcmKey = key;
            }
            return _gcm;
        }

        /// <summary>
        /// 就地清零一段密钥缓冲（null 安全）。废弃 / 轮换会话密钥时由调用方（NetworkManager）
        /// 调用——密钥长期滞留托管堆，堆转储即可提取通道密钥。
        /// </summary>
        /// <param name="key">待清零的密钥缓冲</param>
        public static void ZeroKey(byte[] key)
        {
            if (key == null) return;
            // 用 CryptographicOperations.ZeroMemory 而不是手写循环：前者语义上不会被
            // JIT 优化删除（本类所有调用点都不在 WebGL 路径上，平台可用性不构成问题）。
            CryptographicOperations.ZeroMemory(key);
        }

        /// <summary>就地清零中间缓冲（null 安全；加密/解密用后即弃的副本）。</summary>
        private static void ZeroMemory(byte[] buffer)
        {
            if (buffer == null) return;
            CryptographicOperations.ZeroMemory(buffer);
        }

        /// <summary>
        /// 当前平台是否支持通道加密（首次访问做一次真实加解密自检并缓存）。
        /// 用于决定登录时是否声明 <c>encrypt</c>。
        /// </summary>
        public static bool IsSupported
        {
            get
            {
                var cached = _supported;
                if (cached >= 0) return cached == 1;

                var ok = false;
                try
                {
                    var probe = new byte[KeySize];
                    var enc = Encrypt(probe, new byte[] { 1, 2, 3 });
                    ok = enc != null && TryDecrypt(probe, enc, out var round) && round != null && round.Length == 3;
                }
                catch (Exception e)
                {
                    Game.Logger?.Warn("Crypto", $"platform AES-GCM unavailable: {e.GetType().Name}: {e.Message}");
                }

                _supported = ok ? 1 : 0;
                return ok;
            }
        }

        /// <summary>
        /// 用密钥加密一段完整客户端帧。返回 <c>[nonce][ciphertext+tag]</c>；失败返回 null（调用方不得发送）。
        /// </summary>
        /// <param name="key">32 字节会话密钥</param>
        /// <param name="plaintext">待加密数据（完整客户端帧：8B 帧头 + body）</param>
        public static byte[] Encrypt(byte[] key, byte[] plaintext)
        {
            if (key == null || key.Length != KeySize || plaintext == null) return null;

            // 长度护栏（服务端权威值，见 MaxPlaintextBytes）：超出它的密文服务端必然解不开，
            // 且按「篡改 / 密钥不符」处理会**断连**（不只是丢这一帧）。这里先拦并留痕，
            // 返回 null 即调用方不发（与本方法既有失败契约一致）。
            if (plaintext.Length > MaxPlaintextBytes)
            {
                Game.Logger?.Warn("Crypto",
                    $"channel encrypt refused: plaintext too large ({plaintext.Length} bytes > " +
                    $"MaxPlaintextBytes {MaxPlaintextBytes} = server maxDecryptSize {MaxCiphertextBytes} " +
                    $"- nonce {NonceSize} - tag {TagSize}) — frame not sent");
                return null;
            }

            byte[] cipher = null;
            byte[] tag = null;
            try
            {
                var nonce = new byte[NonceSize];
                // 随机 nonce：与会话密钥一次性使用，nonce 重复会让 GCM 失去安全性。
                using (var rng = RandomNumberGenerator.Create())
                    rng.GetBytes(nonce);

                cipher = new byte[plaintext.Length];
                tag = new byte[TagSize];
                // 复用 AesGcm 实例（密钥不变时不重复构造 + 导入）；加解密统一在锁内。
                lock (GcmGate)
                    GetGcmLocked(key).Encrypt(nonce, plaintext, cipher, tag);

                // 组装为服务端期望的 [nonce][ciphertext][tag]（Go 的 Seal 把 tag 附在密文尾部）。
                var outBuf = new byte[NonceSize + cipher.Length + TagSize];
                Buffer.BlockCopy(nonce, 0, outBuf, 0, NonceSize);
                Buffer.BlockCopy(cipher, 0, outBuf, NonceSize, cipher.Length);
                Buffer.BlockCopy(tag, 0, outBuf, NonceSize + cipher.Length, TagSize);
                return outBuf;
            }
            catch (Exception e)
            {
                Game.Logger?.Error("Crypto", $"channel encrypt failed: {e.Message}", e);
                return null;
            }
            finally
            {
                // 中间副本用后清零（内容已拷进 outBuf，不在堆上留第二份）。
                ZeroMemory(cipher);
                ZeroMemory(tag);
            }
        }

        /// <summary>
        /// 用密钥解密一段密文（<c>[nonce][ciphertext+tag]</c>）。
        /// 认证失败（密钥不符 / 密文被篡改 / 其实是明文帧）返回 false 并输出 null。
        /// </summary>
        /// <param name="key">32 字节会话密钥</param>
        /// <param name="ciphertext">密文</param>
        /// <param name="plaintext">解密结果（失败为 null）</param>
        public static bool TryDecrypt(byte[] key, byte[] ciphertext, out byte[] plaintext)
        {
            plaintext = null;
            if (key == null || key.Length != KeySize || ciphertext == null) return false;
            if (ciphertext.Length < NonceSize + TagSize) return false;

            byte[] cipher = null;
            byte[] tag = null;
            try
            {
                var nonce = new byte[NonceSize];
                Buffer.BlockCopy(ciphertext, 0, nonce, 0, NonceSize);
                var bodyLen = ciphertext.Length - NonceSize - TagSize;
                cipher = new byte[bodyLen];
                tag = new byte[TagSize];
                Buffer.BlockCopy(ciphertext, NonceSize, cipher, 0, bodyLen);
                Buffer.BlockCopy(ciphertext, NonceSize + bodyLen, tag, 0, TagSize);

                var plain = new byte[bodyLen];
                // 复用 AesGcm 实例（同 Encrypt）；加解密统一在锁内。
                lock (GcmGate)
                    GetGcmLocked(key).Decrypt(nonce, cipher, tag, plain);
                plaintext = plain;
                return true;
            }
            catch (CryptographicException)
            {
                // 认证失败是**正常路径**：握手窗口期收到的明文帧也会走到这里（见 NetworkManager.DrainFrame）。
                return false;
            }
            catch (Exception e)
            {
                Game.Logger?.Warn("Crypto", $"channel decrypt error: {e.GetType().Name}: {e.Message}");
                return false;
            }
            finally
            {
                // 中间副本用后清零（输入密文与输出明文的缓冲区不属于本方法，不动）。
                ZeroMemory(cipher);
                ZeroMemory(tag);
            }
        }

        /// <summary>
        /// 从一段回包 body 里提取会话密钥（base64 的 32B）。
        /// 与服务端 <c>auth.ExtractSessionKey</c> 同款做法：只认 <c>session_key</c> 字段、
        /// 与具体 opcode 解耦（回包无消息号，只有 requestID），避免回包结构改名导致加密链路静默失效。
        /// </summary>
        /// <param name="data">帧字节</param>
        /// <param name="offset">body 起始偏移</param>
        /// <param name="length">body 长度</param>
        /// <returns>提取到合法密钥返回 key，否则 null</returns>
        public static byte[] TryExtractKey(byte[] data, int offset, int length)
        {
            if (data == null || length <= 0) return null;

            string value;
            try
            {
                var root = MiniJson.Parse(data, offset, length) as IDictionary<string, object>;
                value = MiniJson.GetString(root, "session_key");
            }
            catch (FormatException e)
            {
                // body 不是 JSON 对象（如二进制体）：本次回包不含密钥，静默跳过。
                Game.Logger?.Debug("Crypto", $"session_key parse skipped: {e.Message}");
                return null;
            }

            if (string.IsNullOrEmpty(value)) return null;
            try
            {
                var key = Convert.FromBase64String(value);
                if (key.Length != KeySize)
                {
                    Game.Logger?.Warn("Crypto", $"session_key is {key.Length} bytes, expected {KeySize} — ignoring");
                    return null;
                }
                return key;
            }
            catch (FormatException)
            {
                // 不是合法 base64：当作"本次回包不含密钥"，静默跳过（服务端不该发这种值）。
                return null;
            }
        }
    }
}
