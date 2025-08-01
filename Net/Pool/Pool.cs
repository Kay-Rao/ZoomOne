namespace ZoomOne.Pool
{


    /// <summary>
    /// 单线程高性能对象池（纯托管实现）
    /// </summary>
    /// <typeparam name="T">池化对象类型</typeparam>
    public sealed class ObjectPool<T> : IDisposable where T : class
    {
        #region 核心实现

        // 使用栈存储对象以获得最佳性能
        private readonly Stack<T> _pool = new Stack<T>();

        // 对象生命周期回调
        private readonly Func<T> _createFunc;
        private readonly Action<T> _onGet;
        private readonly Action<T> _onRelease;
        private readonly Action<T> _onDestroy;
        // 溢出处理回调
        private readonly Func<T> _onOverflow;

        // 配置参数
        private int _maxSize;
        private int _totalCreated;

        /// <summary>
        /// 创建对象池
        /// </summary>
        /// <param name="createFunc">对象创建函数</param>
        /// <param name="onGet">获取对象时的回调</param>
        /// <param name="onRelease">释放对象时的回调</param>
        /// <param name="onDestroy">销毁对象时的回调</param>
        /// <param name="initialSize">初始大小</param>
        /// <param name="maxSize">最大池大小（0=无限制）</param>
        /// <param name="onOverflow">池溢出时的处理函数（可选）</param>
        /// 
        public ObjectPool(
            Func<T> createFunc,
            Action<T> onGet = null,
            Action<T> onRelease = null,
            Action<T> onDestroy = null,
            int initialSize = 0,
            int maxSize = 0,
            Func<T> onOverflow = null)
        {
            _createFunc = createFunc ?? throw new ArgumentNullException(nameof(createFunc));
            _onGet = onGet;
            _onRelease = onRelease;
            _onDestroy = onDestroy;
            _maxSize = maxSize;
            _onOverflow = onOverflow ?? (() => createFunc()); // 默认行为是创建新对象

            Prewarm(initialSize);
        }

        /// <summary>
        /// 从池中获取对象
        /// </summary>
        public T Get()
        {
            // 从池中获取对象
            if (_pool.Count > 0)
            {
                var obj = _pool.Pop();
                _onGet?.Invoke(obj);
                return obj;
            }

            // 池为空时创建新对象
            if (_maxSize <= 0 || _totalCreated < _maxSize)
            {
                var newObj = _createFunc();
                _totalCreated++;
                _onGet?.Invoke(newObj);
                return newObj;
            }

            // 处理池溢出
            return _onOverflow();
        }

        /// <summary>
        /// 将对象返回到池中
        /// </summary>
        public void Release(T obj)
        {
            if (obj == null) return;

            // 执行释放回调
            _onRelease?.Invoke(obj);

            // 如果池已满，则销毁对象
            if (_maxSize > 0 && _pool.Count >= _maxSize)
            {
                DestroyObject(obj);
                return;
            }

            // 将对象放回池中
            _pool.Push(obj);
        }

        /// <summary>
        /// 预分配对象
        /// </summary>
        public void Prewarm(int count)
        {
            int toCreate = count;

            // 考虑最大大小限制
            if (_maxSize > 0)
            {
                toCreate = Math.Min(count, _maxSize - _pool.Count);
            }

            for (int i = 0; i < toCreate; i++)
            {
                var obj = _createFunc();
                _pool.Push(obj);
                _totalCreated++;
            }
        }

        /// <summary>
        /// 清空对象池并销毁所有对象
        /// </summary>
        public void Clear()
        {
            while (_pool.Count > 0)
            {
                var obj = _pool.Pop();
                DestroyObject(obj);
            }
            _totalCreated = 0;
        }

        /// <summary>
        /// 当前池中可用对象数量
        /// </summary>
        public int Count => _pool.Count;

        /// <summary>
        /// 池创建的对象总数
        /// </summary>
        public int TotalCreated => _totalCreated;

        /// <summary>
        /// 池的最大容量
        /// </summary>
        public int MaxSize => _maxSize;



        /// <summary>
        /// 销毁对象
        /// </summary>
        private void DestroyObject(T obj)
        {
            _onDestroy?.Invoke(obj);

            // 如果对象实现了IDisposable，则调用Dispose
            if (obj is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        #endregion

        #region 高级功能

        /// <summary>
        /// 批量获取对象
        /// </summary>
        public void GetBatch(T[] buffer, int count)
        {
            for (int i = 0; i < count; i++)
            {
                buffer[i] = Get();
            }
        }

        /// <summary>
        /// 批量回收对象
        /// </summary>
        public void ReleaseBatch(IEnumerable<T> objects)
        {
            foreach (var obj in objects)
            {
                Release(obj);
            }
        }

        /// <summary>
        /// 调整池的最大大小
        /// </summary>
        public void Resize(int newMaxSize)
        {
            if (newMaxSize < 0)
                throw new ArgumentOutOfRangeException(nameof(newMaxSize));

            // 如果新的大小小于当前池大小，需要销毁多余对象
            if (newMaxSize < _pool.Count)
            {
                int toRemove = _pool.Count - newMaxSize;
                for (int i = 0; i < toRemove; i++)
                {
                    var obj = _pool.Pop();
                    DestroyObject(obj);
                }
            }

            // 更新最大大小
            _maxSize = newMaxSize;
        }

        #endregion

        #region IDisposable 实现

        private bool _disposed;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                Clear();
            }

            _disposed = true;
        }

        ~ObjectPool()
        {
            Dispose(false);
        }

        #endregion
    }

    /// <summary>
    /// 高性能连续内存对象池（适用于值类型）
    /// </summary>
    /// <typeparam name="T">值类型</typeparam>
    public sealed class ValueObjectPool<T> where T : struct
    {
        private readonly T[] _items;
        private readonly int[] _freeIndices;
        private int _freeIndexTop = -1;
        private int _count;

        /// <summary>
        /// 创建连续内存对象池
        /// </summary>
        /// <param name="capacity">池容量</param>
        public ValueObjectPool(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));

            _items = new T[capacity];
            _freeIndices = new int[capacity];

            // 初始化空闲索引
            for (int i = 0; i < capacity; i++)
            {
                _freeIndices[i] = i;
            }
            _freeIndexTop = capacity - 1;
        }

        /// <summary>
        /// 获取对象引用
        /// </summary>
        /// <param name="item">返回的对象引用</param>
        /// <param name="index">对象索引</param>
        /// <returns>是否成功获取</returns>
        public bool TryGet(out T item, out int index)
        {
            if (_freeIndexTop >= 0)
            {
                index = _freeIndices[_freeIndexTop--];
                item = _items[index];
                return true;
            }

            item = default;
            index = -1;
            return false;
        }

        /// <summary>
        /// 释放对象
        /// </summary>
        public void Release(int index)
        {
            if (index < 0 || index >= _items.Length)
                throw new IndexOutOfRangeException();

            _freeIndices[++_freeIndexTop] = index;
        }

        /// <summary>
        /// 直接访问底层数组
        /// </summary>
        public T[] Items => _items;

        /// <summary>
        /// 池容量
        /// </summary>
        public int Capacity => _items.Length;

        /// <summary>
        /// 当前可用对象数量
        /// </summary>
        public int FreeCount => _freeIndexTop + 1;

        /// <summary>
        /// 当前已使用对象数量
        /// </summary>
        public int UsedCount => _items.Length - (_freeIndexTop + 1);
    }

    /// <summary>
    /// 自动释放包装器
    /// </summary>
    /// <typeparam name="T">池化对象类型</typeparam>
    public readonly struct PooledObject<T> : IDisposable where T : class
    {
        private readonly ObjectPool<T> _pool;
        public T Value { get; }

        public PooledObject(ObjectPool<T> pool, T value)
        {
            _pool = pool;
            Value = value;
        }

        public void Dispose()
        {
            _pool.Release(Value);
        }
    }

    /// <summary>
    /// 对象池扩展方法
    /// </summary>
    public static class ObjectPoolExtensions
    {
        /// <summary>
        /// 获取带自动释放的对象
        /// </summary>
        public static PooledObject<T> GetAutoRelease<T>(
            this ObjectPool<T> pool) where T : class
        {
            return new PooledObject<T>(pool, pool.Get());
        }
    }
}