using System;
using System.Collections.Concurrent;

namespace CloverEngine
{
    /// <summary>
    /// 异步任务分发器接口，用于将操作排队到主线程执行。
    /// </summary>
    public interface IDispatcher
    {
        /// <summary>
        /// 将一个操作加入队列，待主线程执行。
        /// </summary>
        /// <param name="action">要执行的操作。</param>
        void Post(Action action);

        /// <summary>
        /// 执行队列中的所有待执行操作。
        /// </summary>
        void Flush();
    }

    /// <summary>
    /// 异步任务分发器的内部实现，使用线程安全的队列管理待执行操作。
    /// </summary>
    internal class Dispatcher : IDispatcher
    {
        /// <summary>
        /// 单次 <see cref="Flush"/> 最多执行的任务数（含回调执行期间新投递的任务）。
        /// <para>
        /// 旧实现用 while 把队列排空：回调里持续 <see cref="Post"/> 会在单帧内无限循环、
        /// 卡死主线程。超过上限的任务按 FIFO 留到下一帧继续执行（不丢任务）。
        /// 队列本身无上限 —— 若生产端（后台线程）持续高频 Post 超过消费速度，
        /// 会表现为"每帧都触顶 + 队列缓慢增长"，由告警日志暴露，需要生产端自行限流。
        /// </para>
        /// </summary>
        private const int MaxFlushPerFrame = 1024;

        private readonly ConcurrentQueue<Action> _queue = new();

        /// <summary>触顶次数（单线程使用：Flush 只在主线程调用）。</summary>
        private int _overflowCount;

        /// <summary>
        /// 将一个操作加入队列，待主线程执行。
        /// </summary>
        /// <param name="action">要执行的操作。</param>
        public void Post(Action action)
        {
            _queue.Enqueue(action);
        }

        /// <summary>
        /// 依次执行队列中的操作（单帧最多 <see cref="MaxFlushPerFrame"/> 个），并捕获执行过程中的异常。
        /// <para>触顶时记 Warn（首次必报，之后每 100 次报一次）：剩余任务留到下一帧，不会逐帧刷屏。</para>
        /// </summary>
        public void Flush()
        {
            var processed = 0;
            while (processed < MaxFlushPerFrame && _queue.TryDequeue(out var action))
            {
                processed++;
                try
                {
                    action?.Invoke();
                }
                catch (Exception ex)
                {
                    Game.Logger?.Error("Dispatcher", $"Action error: {ex.Message}", ex);
                }
            }

            if (processed >= MaxFlushPerFrame && !_queue.IsEmpty)
            {
                _overflowCount++;
                if (_overflowCount == 1 || _overflowCount % 100 == 0)
                {
                    Game.Logger?.Warn("Dispatcher",
                        $"flush hit the per-frame cap ({MaxFlushPerFrame}); remaining tasks deferred to the next frame " +
                        $"(occurrence {_overflowCount}) - a callback is likely re-posting every frame");
                }
            }
        }
    }
}
