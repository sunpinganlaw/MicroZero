﻿using ZeroMQ.lib;

namespace ZeroMQ
{
    /// <summary>
    /// Linux平台使用
    /// </summary>
    public unsafe class LinuxZPoll : MemoryCheck, IZmqPool
    {
        /// <summary>
        /// 对应的Socket集合
        /// </summary>
        public ZSocket[] Sockets { get; set; }

        /// <summary>
        /// Sockt数量
        /// </summary>
        public int Size { get; set; }

        /// <summary>
        /// 超时
        /// </summary>
        public int TimeoutMs { get; set; } = 1000;
        private ZError error;

        /// <summary>
        /// 错误对象
        /// </summary>
        public ZError ZError => error ?? (error = ZError.GetLastErr());

        /// <summary>
        /// 非托管句柄
        /// </summary>
        public DispoIntPtr Ptr { get; set; }

        /// <summary>
        /// 准备
        /// </summary>
        /// <param name="sockets"></param>
        /// <param name="events"></param>
        public void Prepare(ZSocket[] sockets, ZPollEvent events)
        {
            Sockets = sockets;
            error = null;
            Size = sockets.Length;
            Ptr = DispoIntPtr.Alloc(sizeof(zmq_pollitem_posix_t) * sockets.Length);
            zmq_pollitem_posix_t* natives = (zmq_pollitem_posix_t*)Ptr.Ptr;
            for (int i = 0; i < Size; ++i)
            {
                zmq_pollitem_posix_t* native = natives + i;
                native->SocketPtr = sockets[i].SocketPtr;
                native->Events = (short)(events);
                native->ReadyEvents = (short)ZPollEvent.None;
            }
        }
        /// <summary>
        /// 准备
        /// </summary>
        /// <param name="sockets"></param>
        /// <param name="events"></param>
        public void Prepare(ZPollEvent events, params ZSocket[] sockets)
        {
            Sockets = sockets;
            error = null;
            Size = sockets.Length;
            Ptr = DispoIntPtr.Alloc(sizeof(zmq_pollitem_posix_t) * sockets.Length);
            zmq_pollitem_posix_t* natives = (zmq_pollitem_posix_t*)Ptr.Ptr;
            for (int i = 0; i < Size; ++i)
            {
                zmq_pollitem_posix_t* native = natives + i;
                native->SocketPtr = sockets[i].SocketPtr;
                native->Events = (short)(events);
                native->ReadyEvents = (short)ZPollEvent.None;
            }
        }

        /// <summary>
        /// 一次Pool
        /// </summary>
        /// <returns></returns>
        public bool Poll()
        {
            error = null;

            zmq_pollitem_posix_t* natives = (zmq_pollitem_posix_t*)Ptr.Ptr;
            var state = zmq.poll(natives, Size, TimeoutMs);
            return state != -1;
        }

        /// <summary>
        /// 检查下标是否有数据
        /// </summary>
        /// <param name="index"></param>
        /// <param name="message"></param>
        public bool CheckIn(int index, out ZMessage message)
        {
            zmq_pollitem_posix_t* native = ((zmq_pollitem_posix_t*)Ptr.Ptr) + index;
            if (native->ReadyEvents == 0)
            {
                message = null;
                return false;
            }
            if (!((ZPollEvent)native->ReadyEvents).HasFlag(ZPollEvent.In))
            {
                message = null;
                return false;
            }
            return Sockets[index].Recv(out message, 1);
        }

        /// <summary>
        /// 检查下标是否有数据
        /// </summary>
        /// <param name="index"></param>
        /// <param name="message"></param>
        /// <returns></returns>
        public bool CheckOut(int index, out ZMessage message)
        {
            zmq_pollitem_posix_t* native = ((zmq_pollitem_posix_t*)Ptr.Ptr) + index;
            if (native->ReadyEvents == 0)
            {
                message = null;
                return false;
            }
            if (!((ZPollEvent)native->ReadyEvents).HasFlag(ZPollEvent.Out))
            {
                message = null;
                return false;
            }
            return Sockets[index].Recv(out message, 1);
        }


        /// <summary>
        /// 析构
        /// </summary>
        protected override void DoDispose()
        {
            Ptr.Dispose();
            foreach (var socket in Sockets)
            {
                socket.Close();
                socket.Dispose();
            }
        }
    }
}