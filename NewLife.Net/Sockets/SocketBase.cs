﻿using System;
using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using NewLife.Collections;
using NewLife.Exceptions;
using NewLife.Net.Common;
using NewLife.Reflection;
using NewLife.Threading;

namespace NewLife.Net.Sockets
{
    /// <summary>Socket基类</summary>
    /// <remarks>
    /// 主要是对Socket封装一层，把所有异步操作结果转移到事件中<see cref="Completed"/>去。
    /// 参数池<see cref="Pool"/>维护这所有事件参数，借出<see cref="Pop"/>时挂接<see cref="Completed"/>事件，
    /// 归还<see cref="Push"/>时，取消<see cref="Completed"/>事件。
    /// </remarks>
    public class SocketBase : Netbase, ISocket
    {
        #region 属性
        private Socket _Socket;
        /// <summary>套接字</summary>
        internal protected Socket Socket
        {
            get { return _Socket; }
            set
            {
                if (value != null)
                {
                    if (_Socket != value)
                    {
                        _LocalEndPoint = null;
                        _RemoteEndPoint = null;
                    }
                    OnSocketChange(value);
                }

                _Socket = value;
            }
        }

        /// <summary>基础Socket对象</summary>
        Socket ISocket.Socket { get { return Socket; } set { Socket = value; } }

        private ProtocolType _ProtocolType = ProtocolType.Tcp;
        /// <summary>协议类型</summary>
        public virtual ProtocolType ProtocolType { get { return _ProtocolType; } }

        private IPAddress _Address = IPAddress.Any;
        /// <summary>监听本地地址</summary>
        public IPAddress Address
        {
            get
            {
                var socket = Socket;
                try
                {
                    if (socket != null && socket.LocalEndPoint is IPEndPoint) _Address = (socket.LocalEndPoint as IPEndPoint).Address;
                }
                catch (ObjectDisposedException) { }

                return _Address;
            }
            set
            {
                _Address = value;
                if (value != null) AddressFamily = value.AddressFamily;
            }
        }

        private Int32 _Port;
        /// <summary>监听本地端口</summary>
        public Int32 Port
        {
            get
            {
                var socket = Socket;
                try
                {
                    if (socket != null && socket.LocalEndPoint is IPEndPoint) _Port = (socket.LocalEndPoint as IPEndPoint).Port;
                }
                catch (ObjectDisposedException) { }

                return _Port;
            }
            set { _Port = value; }
        }

        private AddressFamily _AddressFamily = AddressFamily.InterNetwork;
        /// <summary>本地地址族</summary>
        public AddressFamily AddressFamily
        {
            get { return _AddressFamily; }
            set
            {
                _AddressFamily = value;

                // 根据地址族选择合适的本地地址
                _Address = _Address.GetRightAny(value);
            }
        }

        private IPEndPoint _LocalEndPoint;
        /// <summary>本地终结点</summary>
        public IPEndPoint LocalEndPoint
        {
            get
            {
                if (_LocalEndPoint != null) return _LocalEndPoint;

                var socket = Socket;
                try
                {
                    if (socket != null) _LocalEndPoint = socket.LocalEndPoint as IPEndPoint;
                }
                catch (ObjectDisposedException) { }

                return _LocalEndPoint ?? new IPEndPoint(Address, Port);
            }
        }

        private IPEndPoint _RemoteEndPoint;
        /// <summary>远程终结点</summary>
        public IPEndPoint RemoteEndPoint
        {
            get
            {
                if (_RemoteEndPoint != null) return _RemoteEndPoint;

                var socket = Socket;
                if (socket != null && socket.Connected)
                {
                    _RemoteEndPoint = socket.RemoteEndPoint as IPEndPoint;
                }

                return _RemoteEndPoint;
            }
        }

        /// <summary>本地地址</summary>
        public NetUri LocalUri { get { return new NetUri(ProtocolType, LocalEndPoint); } }

        /// <summary>远程地址</summary>
        public NetUri RemoteUri { get { return new NetUri(ProtocolType, RemoteEndPoint); } }

        //private Int32 _BufferSize = 10240;
        private Int32 _BufferSize = 102400;
        /// <summary>缓冲区大小</summary>
        public Int32 BufferSize { get { return _BufferSize; } set { _BufferSize = value; } }

        private Boolean _NoDelay = true;
        /// <summary>禁用接收延迟，收到数据后马上建立异步读取再处理本次数据</summary>
        public Boolean NoDelay { get { return _NoDelay; } set { _NoDelay = value; } }

        private Boolean _UseThreadPool;
        /// <summary>是否使用线程池处理事件。建议仅在事件处理非常耗时时使用线程池来处理。</summary>
        public Boolean UseThreadPool { get { return _UseThreadPool; } set { _UseThreadPool = value; } }
        #endregion

        #region 扩展属性
        private Boolean _ReuseAddress;
        /// <summary>允许将套接字绑定到已在使用中的地址。</summary>
        public Boolean ReuseAddress
        {
            get
            {
                if (Socket == null) return _ReuseAddress;

                Object value = Socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress);
                return Convert.ToBoolean(value);
            }
            set
            {
                if (Socket != null) Socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, value);
                _ReuseAddress = value;
            }
        }

        private static MemberInfoX[] _mSafeHandle;
        /// <summary>SafeHandle字段</summary>
        private static MemberInfoX mSafeHandle
        {
            get
            {
                if (_mSafeHandle != null && _mSafeHandle.Length > 0) return _mSafeHandle[0];

                MemberInfoX pix = FieldInfoX.Create(typeof(Socket), "m_Handle");
                if (pix == null) pix = PropertyInfoX.Create(typeof(Socket), "SafeHandle");
                _mSafeHandle = new MemberInfoX[] { pix };

                return pix;
            }
        }

        /// <summary>安全句柄</summary>
        private SafeHandle SafeHandle
        {
            get
            {
                if (Socket == null) return null;

                var pix = mSafeHandle;
                if (pix != null) return pix.GetValue(Socket) as SafeHandle;

                return null;
            }
        }

        private IDictionary _Items;
        /// <summary>数据字典</summary>
        public IDictionary Items { get { return _Items ?? (_Items = new Hashtable(StringComparer.OrdinalIgnoreCase)); } }
        #endregion

        #region 构造
        static SocketBase()
        {
            // 注册接口实现
            NetService.Install();

            // 自动收缩内存，调试状态1分钟一次，非调试状态10分钟一次
            Int32 time = NetHelper.Debug ? 60000 : 600000;
            new TimerX(s => Runtime.ReleaseMemory(), null, time, time, false);

            try
            {
                // 写在另外一个方法里面，保证不会在当前方法编译的时候就报错
                CheckNet21();
            }
            catch (TypeLoadException ex)
            {
                if (ex.TypeName.Contains("SocketAsyncEventArgs"))
                    throw new XException("NewLife.Net网络库需要.Net2.0 Sp1支持！", ex);
                else
                    throw ex;
            }
        }

        static void CheckNet21()
        {
            var e = new SocketAsyncEventArgs();
            e.Dispose();
        }

        /// <summary>实例化</summary>
        public SocketBase() { SetShowEventLog(); }
        #endregion

        #region 释放资源
        /// <summary>关闭网络操作</summary>
        public void Close() { Dispose(); }

        /// <summary>子类重载实现资源释放逻辑</summary>
        /// <param name="disposing">从Dispose调用（释放所有资源）还是析构函数调用（释放非托管资源）</param>
        protected override void OnDispose(bool disposing)
        {
            base.OnDispose(disposing);

            var socket = Socket;
            if (socket == null) return;

            var hand = SafeHandle;
            if (hand != null && !hand.IsClosed)
            {
                // 先用Shutdown禁用Socket（发送未完成发送的数据），再用Close关闭，这是一种比较优雅的关闭Socket的方法
                if (socket.Connected)
                {
                    try
                    {
                        socket.Disconnect(ReuseAddress);
                        socket.Shutdown(SocketShutdown.Both);
                    }
                    catch (SocketException ex2)
                    {
                        if (ex2.SocketErrorCode != SocketError.NotConnected) WriteLog(ex2.ToString());
                    }
                    catch (ObjectDisposedException) { }
                    catch (Exception ex3)
                    {
                        if (NetHelper.Debug) WriteLog(ex3.ToString());
                    }
                }

                socket.Close();
            }
            Socket = null;

            if (_Statistics != null)
            {
                IDisposable dp = _Statistics as IDisposable;
                if (dp != null) dp.Dispose();
                _Statistics = null;
            }
        }
        #endregion

        #region 方法
        /// <summary>确保创建基础Socket对象</summary>
        protected virtual void EnsureCreate()
        {
            if (Socket != null || Disposed) return;

            switch (ProtocolType)
            {
                case ProtocolType.Tcp:
                    Socket = new Socket(AddressFamily, SocketType.Stream, ProtocolType);
                    Socket.SetTcpKeepAlive(true);
                    break;
                case ProtocolType.Udp:
                    Socket = new Socket(AddressFamily, SocketType.Dgram, ProtocolType);
                    break;
                default:
                    Socket = new Socket(AddressFamily, SocketType.Unknown, ProtocolType);
                    break;
            }

            // 设置超时时间
            Socket.SendTimeout = 10000;
            Socket.ReceiveTimeout = 10000;

            if (_ReuseAddress) ReuseAddress = true;
        }

        /// <summary>绑定本地终结点</summary>
        public virtual void Bind()
        {
            EnsureCreate();
            var socket = Socket;
            if (socket != null && !socket.IsBound)
            {
                socket.Bind(new IPEndPoint(Address, Port));

                OnSocketChange(socket);
            }
        }

        /// <summary>获取相对于指定远程地址的本地地址</summary>
        /// <param name="remote"></param>
        /// <returns></returns>
        public IPAddress GetRelativeAddress(IPAddress remote)
        {
            // 如果不是任意地址，直接返回
            var addr = LocalEndPoint.Address;
            if (!addr.IsAny()) return addr;

            // 如果是本地环回地址，返回环回地址
            if (IPAddress.IsLoopback(remote))
                return addr.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Loopback : IPAddress.IPv6Loopback;

            // 否则返回本地第一个IP地址
            foreach (var item in NetHelper.GetIPs())
            {
                if (item.AddressFamily == addr.AddressFamily) return item;
            }
            return null;
        }

        /// <summary>获取相对于指定远程地址的本地地址</summary>
        /// <param name="remote"></param>
        /// <returns></returns>
        public IPEndPoint GetRelativeEndPoint(IPAddress remote)
        {
            var addr = GetRelativeAddress(remote);
            return addr == null ? LocalEndPoint : new IPEndPoint(addr, LocalEndPoint.Port);
        }

        void OnSocketChange(Socket socket)
        {
            if (socket == null) return;

            _ProtocolType = socket.ProtocolType;
            _AddressFamily = socket.AddressFamily;

            if (_LocalEndPoint == null)
            {
                try
                {
                    var ep = socket.LocalEndPoint as IPEndPoint;
                    if (ep != null)
                    {
                        _Address = ep.Address;
                        _Port = ep.Port;
                    }
                    _LocalEndPoint = ep;
                }
                catch (ObjectDisposedException) { }
            }

            if (_RemoteEndPoint == null)
            {
                if (socket.Connected) _RemoteEndPoint = socket.RemoteEndPoint as IPEndPoint;
            }
        }

        private Int32 _AsyncCount;
        /// <summary>异步操作计数</summary>
        public Int32 AsyncCount { get { return _AsyncCount; } /*set { _AsyncCount = value; }*/ }

        /// <summary>开始异步操作</summary>
        /// <param name="callback"></param>
        /// <param name="e"></param>
        /// <param name="needBuffer">是否需要缓冲区，默认需要，只有Accept不需要</param>
        internal protected void StartAsync(Func<NetEventArgs, Boolean> callback, NetEventArgs e, Boolean needBuffer = true)
        {
            EnsureCreate();
            if (Socket == null) return;
            if (!Socket.IsBound) Bind();

            // 如果没有传入网络事件参数，从对象池借用
            if (e == null) e = Pop();

            e.SetBuffer(needBuffer ? BufferSize : 0);

            try
            {
                // 如果立即返回，则异步处理完成事件
                if (!callback(e)) RaiseCompleteAsync(e);

                // 异步开始，增加一个计数
                //AsyncCount++;
                Interlocked.Increment(ref _AsyncCount);
            }
            catch
            {
                // 如果callback或RaiseCompleteAsync成功，都将由里面的方法负责回收参数；
                // 否则，这里需要自己回收参数。
                Push(e);
                throw;
            }
        }
        #endregion

        #region 套接字事件参数池
        private static ObjectPool<NetEventArgs> _Pool;
        /// <summary>套接字事件参数池。静态，所有实例共享使用</summary>
        static ObjectPool<NetEventArgs> Pool { get { return _Pool ?? (_Pool = new ObjectPool<NetEventArgs>() { Max = 100 }); } }

        /// <summary>从池里拿一个对象。回收原则参考<see cref="Push"/></summary>
        /// <returns></returns>
        public NetEventArgs Pop()
        {
            NetEventArgs e = Pool.Pop();
            if (e.Used) throw new Exception("才刚出炉，怎么可能使用中呢？");

            e.AcceptSocket = Socket;
            //e.UserToken = this;
            e.Socket = this;
            e.Completed += OnCompleted;
            //if (e.Buffer == null) e.SetBuffer(BufferSize);

            e.Times++;
            e.Used = true;

            return e;
        }

        /// <summary>把对象归还到池里</summary>
        /// <remarks>
        /// 网络事件参数使用原则：
        /// 1，得到者负责回收（通过方法参数得到）
        /// 2，正常执行时自己负责回收，异常时顶级或OnError负责回收
        /// 3，把回收责任交给别的方法
        /// 4，事件订阅者不允许回收，不允许另作他用
        /// </remarks>
        /// <param name="e"></param>
        public void Push(NetEventArgs e)
        {
            if (e == null) return;
            if (!e.Used) throw new Exception("准备回炉，怎么可能已经不再使用呢？");

            e.Error = null;
            e.UserToken = null;
            e.Socket = null;
            e.Session = null;
            e.AcceptSocket = null;
            e.RemoteEndPoint = null;
            e.Completed -= OnCompleted;

            // 清空缓冲区，避免事件池里面的对象占用内存
            e.SetBuffer(0);

            e.Used = false;

            Pool.Push(e);
        }
        #endregion

        #region 完成事件
        /// <summary>完成事件，将在工作线程中被调用，不要占用太多时间。</summary>
        public event EventHandler<NetEventArgs> Completed;

        private void OnCompleted(Object sender, SocketAsyncEventArgs e)
        {
            // 异步完成，减少一个计数
            //AsyncCount--;
            Interlocked.Decrement(ref _AsyncCount);

            if (e is NetEventArgs)
                RaiseComplete(e as NetEventArgs);
            else
                throw new InvalidOperationException("所有套接字事件参数必须来自于事件参数池Pool！");
        }

        /// <summary>触发完成事件。
        /// 可能由工作线程（事件触发）调用，也可能由用户线程通过线程池线程调用。
        /// 作为顶级，将会处理所有异常并调用OnError，其中OnError有能力回收参数e。
        /// </summary>
        /// <param name="e"></param>
        protected void RaiseComplete(NetEventArgs e)
        {
            if (ShowEventLog && EnableLog) ShowEvent(e);

            try
            {
                if (Completed != null)
                {
                    e.Cancel = false;
                    Completed(this, e);
                    if (e.Cancel) return;
                }

                OnComplete(e);
                // 这里可以改造为使用多线程处理事件
                //ThreadPoolCallback(OnCompleted, e);
            }
            catch (Exception ex)
            {
                OnError(e, ex);
                // 都是在线程池线程里面了，不要往外抛出异常
                //throw;
            }
        }

        private Boolean _ShowEventLog;
        /// <summary>是否显示事件日志</summary>
        public Boolean ShowEventLog { get { return _ShowEventLog; } set { _ShowEventLog = value; } }

        [Conditional("DEBUG")]
        void SetShowEventLog() { ShowEventLog = true; }

        void ShowEvent(NetEventArgs e)
        {
            //WriteLog("Completed[{4}] {0} {1} {2} [{3}]", this, e.LastOperation, e.SocketError, e.BytesTransferred, e.ID);
            var sb = new StringBuilder();
            sb.AppendFormat("[{0}] {1}://{2}", e.ID, ProtocolType, LocalEndPoint);
            var ep = e.RemoteIPEndPoint;
            if (ep == null || ep.Address.IsAny()) ep = RemoteEndPoint;
            if ((ep == null || ep.Address.IsAny()) && e.LastOperation == SocketAsyncOperation.Accept && e.AcceptSocket != null) ep = e.AcceptSocket.RemoteEndPoint as IPEndPoint;
            if (ep != null && !ep.Address.IsAny()) sb.AppendFormat("=>{0}", ep);
            sb.AppendFormat(" {0}", e.LastOperation);
            if (e.SocketError != SocketError.Success) sb.AppendFormat(" {0}", e.SocketError);
            sb.AppendFormat(" [{0}]", e.BytesTransferred);
            WriteLog(sb.ToString());
        }

        /// <summary>异步触发完成事件处理程序</summary>
        /// <param name="e"></param>
        protected void RaiseCompleteAsync(NetEventArgs e)
        {
            ThreadPool.QueueUserWorkItem(delegate(Object state)
            {
                RaiseComplete(state as NetEventArgs);
            }, e);
        }

        /// <summary>完成事件分发中心。
        /// 正常执行时OnComplete必须保证回收参数e，异常时RaiseComplete将能够代为回收
        /// </summary>
        /// <param name="e"></param>
        protected virtual void OnComplete(NetEventArgs e) { }
        #endregion

        #region 异步结果处理
        /// <summary>处理异步结果。重点涉及<see cref="NoDelay"/>。内部负责回收参数</summary>
        /// <param name="e">事件参数</param>
        /// <param name="start">开始新异步操作的委托</param>
        /// <param name="process">处理结果的委托</param>
        protected virtual void Process(NetEventArgs e, Action<NetEventArgs> start, Action<NetEventArgs> process)
        {
            if (UseThreadPool)
            {
                ThreadPool.QueueUserWorkItem(s =>
                {
                    OnProcess(e, start, process);
                });
            }
            else
            {
                OnProcess(e, start, process);
            }
        }

        void OnProcess(NetEventArgs e, Action<NetEventArgs> start, Action<NetEventArgs> process)
        {
            // 再次开始
            if (NoDelay && e.SocketError != SocketError.OperationAborted) start(null);

            // Socket错误由各个处理器来处理
            if (e.SocketError != SocketError.Success)
            {
                OnError(e, null);
                return;
            }

            try
            {
                // 业务处理
                process(e);

                if (NoDelay)
                    Push(e);
                else
                    start(e);
            }
            catch (Exception ex)
            {
                try
                {
                    OnError(e, ex);
                }
                catch { }
            }
        }
        #endregion

        #region 错误处理
        /// <summary>错误发生/断开连接时</summary>
        public event EventHandler<NetEventArgs> Error;

        /// <summary>错误发生/断开连接时。拦截Error事件中的所有异常，不外抛，防止因为Error异常导致多次调用OnError</summary>
        /// <param name="e"></param>
        /// <param name="ex"></param>
        protected void ProcessError(NetEventArgs e, Exception ex)
        {
            if (Error != null)
            {
                if (ex != null)
                {
                    if (e == null) e = Pop();
                    e.Error = ex;
                }

                try
                {
                    Error(this, e);
                }
                catch (Exception ex2)
                {
                    WriteLog(ex2.ToString());
                }
            }

            // 不管有没有外部事件，都要归还网络事件参数，那是对象池的东西，不是你的
            if (e != null) Push(e);
        }

        /// <summary>错误发生时。负责调用Error事件以及回收网络事件参数</summary>
        /// <remarks>OnError除了会调用ProcessError外，还会关闭Socket</remarks>
        /// <param name="e"></param>
        /// <param name="ex"></param>
        protected virtual void OnError(NetEventArgs e, Exception ex)
        {
            try
            {
                ProcessError(e, ex);
            }
            finally
            {
                Close();
            }
        }
        #endregion

        #region 辅助
        /// <summary>检查缓冲区大小</summary>
        /// <param name="e"></param>
        [Conditional("DEBUG")]
        internal protected void CheckBufferSize(NetEventArgs e)
        {
            //Int32 n = e.BytesTransferred;
            //if (n >= e.Buffer.Length || ProtocolType == ProtocolType.Tcp && n >= 1460 || ProtocolType == ProtocolType.Udp && n >= 1464)
            //{
            //    WriteLog("接收的实际数据大小{0}超过了缓冲区大小，需要根据真实MTU调整缓冲区大小以提高效率！", n);
            //}
        }

        /// <summary>已重载。</summary>
        /// <returns></returns>
        public override string ToString()
        {
            //if (RemoteEndPoint == null)
            return String.Format("{0}://{1}", ProtocolType, LocalEndPoint);
            //else
            //    return String.Format("{0}://{1}=>{2}", ProtocolType, LocalEndPoint, RemoteEndPoint);
        }
        #endregion

        #region 统计
        private IStatistics _Statistics;
        /// <summary>统计信息，默认关闭，通过<see cref="IStatistics.Enable"/>打开。</summary>
        public IStatistics Statistics { get { return _Statistics ?? (_Statistics = NetService.Resolve<IStatistics>()); } }

        /// <summary>增加计数</summary>
        protected void IncCounter() { Statistics.Increment(); }
        #endregion
    }
}