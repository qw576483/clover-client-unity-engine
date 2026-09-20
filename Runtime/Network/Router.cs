using System;
using System.Collections.Generic;

namespace CloverEngine
{
    /// <summary>
    /// 消息路由实现，负责消息的注册、注销和分发。
    /// 同一消息 ID 可注册多个处理器，分发时按注册顺序依次调用，单个处理器异常不影响其他处理器。
    /// 契约类型（NetCtx / Serializer / MsgHandler / IRouter）定义于 Core 程序集 Contracts.cs。
    /// 方法名与 IRouter / 服务端 g.OnMsg 统一为 OnMsg / OffMsg。
    /// </summary>
    internal class Router : IRouter
    {
        private readonly Dictionary<uint, List<MsgHandler>> _handlers = new();

        /// <summary>
        /// 注册指定消息 ID 的处理器，同一消息可注册多个处理器
        /// </summary>
        /// <param name="msgID">消息 ID</param>
        /// <param name="handler">消息处理器</param>
        public void OnMsg(uint msgID, MsgHandler handler)
        {
            if (!_handlers.TryGetValue(msgID, out var list))
            {
                list = new List<MsgHandler>();
                _handlers[msgID] = list;
            }
            list.Add(handler);
        }

        /// <summary>
        /// 注销指定消息 ID 的所有处理器
        /// </summary>
        /// <param name="msgID">消息 ID</param>
        public void OffMsg(uint msgID)
        {
            _handlers.Remove(msgID);
        }

        /// <summary>
        /// 注销指定消息 ID 的特定处理器
        /// </summary>
        /// <param name="msgID">消息 ID</param>
        /// <param name="handler">要移除的处理器</param>
        public void OffMsg(uint msgID, MsgHandler handler)
        {
            if (_handlers.TryGetValue(msgID, out var list))
                list.Remove(handler);
        }

        /// <summary>
        /// 分发指定消息给所有已注册的处理器，单个处理器异常不影响其他处理器
        /// </summary>
        /// <param name="msgID">消息 ID</param>
        /// <param name="requestID">请求 ID</param>
        /// <param name="traceID">追踪 ID</param>
        /// <param name="body">消息正文原始数据</param>
        /// <param name="offset">正文数据起始偏移量</param>
        /// <param name="length">正文数据长度</param>
        public void Dispatch(uint msgID, uint requestID, string traceID, byte[] body, int offset, int length)
        {
            if (!_handlers.TryGetValue(msgID, out var list)) return;

            var ctx = new NetCtx
            {
                MsgID = msgID,
                RequestID = requestID,
                TraceID = traceID,
                Body = body,
                BodyOffset = offset,
                BodyLength = length
            };

            // 修复bug：在遍历前复制列表，避免handler中注册新handler导致InvalidOperationException
            var handlersCopy = new List<MsgHandler>(list);
            foreach (var handler in handlersCopy)
            {
                try
                {
                    handler.Invoke(ctx);
                }
                catch (Exception ex)
                {
                    Game.Logger?.Error("Router", $"Handler error [msgID={msgID}]: {ex.Message}", ex);
                }
            }
        }
    }
}
