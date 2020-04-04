using IonKiwi.Extensions;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace IonKiwi.Threading {
	public sealed class AsyncMonitor {
		private readonly object _syncRoot = new object();
		private readonly List<(TaskCompletionSource<object> async, object sync)> _queue = new List<(TaskCompletionSource<object> async, object sync)>();

		private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
#if NETSTANDARD2_0
		private readonly Task<IDisposable> _releaserTask;
		private readonly Task<IDisposable> _reentrantReleaserTask;
#endif
		private readonly IDisposable _releaser;
		private readonly IDisposable _reentrantReleaser;
		private readonly Guid _lockId;
		private readonly AsyncLocal<Guid?> _threadId;
		private Guid? _lockHolder = null;
		private int _lockCount = 0;

		public AsyncMonitor() {
			_releaser = new Releaser(this);
			_reentrantReleaser = new ReentrantReleaser(this);
#if NETSTANDARD2_0
			_releaserTask = Task.FromResult(_releaser);
			_reentrantReleaserTask = Task.FromResult(_reentrantReleaser);
#endif
			_lockId = Guid.NewGuid();
			_threadId = new AsyncLocal<Guid?>();
		}

		internal Guid LockId => _lockId;

		internal bool HasLock {
			get {
				var l = _lockHolder;
				return l.HasValue && l.Value == _threadId.Value;
			}
		}

		internal int LockCount => _lockCount;

#if NETSTANDARD2_0
		public Task<IDisposable> EnterAsync() {
#else
		public ValueTask<IDisposable> EnterAsync() {
#endif
			// NOTE: very important! this method must NOT be async
			//       or the AsyncLock re-entry feature will not work
			//       the AsyncLocal inside LockAsync must be set before being awaited

			Guid? lockContext = _lockHolder;
			if (lockContext.HasValue && lockContext.Value == _threadId.Value) {
				// re-entry
				_lockCount++;
#if NETSTANDARD2_0
				return _reentrantReleaserTask;
#else
				return new ValueTask<IDisposable>(_reentrantReleaser);
#endif
			}
			var threadId = Guid.NewGuid();
			_threadId.Value = threadId;
			var wait = _semaphore.WaitAsync();
			if (wait.IsCompleted) {
				if (_lockCount != 0) { throw new Exception("Internal state corruption"); }
				_lockHolder = threadId;
				_lockCount = 1;
#if NETSTANDARD2_0
				return _releaserTask;
#else
				return new ValueTask<IDisposable>(_releaser);
#endif
			}
#if NETSTANDARD2_0
			return wait.ContinueWith((Func<Task, object, IDisposable>)LockAsyncWaitCompleted, threadId, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
#else
			return new ValueTask<IDisposable>(wait.ContinueWith((Func<Task, object, IDisposable>)LockAsyncWaitCompleted, threadId, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
#endif
		}

		public IDisposable Enter() {
			Guid? lockContext = _lockHolder;
			if (lockContext.HasValue && lockContext.Value == _threadId.Value) {
				// re-entry
				_lockCount++;
				return _reentrantReleaser;
			}
			var threadId = Guid.NewGuid();
			_threadId.Value = threadId;
			_semaphore.Wait();
			if (_lockCount != 0) { throw new Exception("Internal state corruption"); }
			_lockHolder = threadId;
			_lockCount = 1;
			return _releaser;
		}

		public void Pulse() {
			if (!HasLock) { throw new InvalidOperationException("Aquire lock first"); }

			(TaskCompletionSource<object> async, object sync)? item = null;
			lock (_syncRoot) {
				if (_queue.Count > 0) {
					item = _queue[0];
					_queue.RemoveAt(0);
				}
			}

			if (item.HasValue) {
				var item2 = item.Value;
				if (item2.async != null) {
					item2.async.TrySetResult(null);
				}
				if (item2.sync != null) {
					lock (item2.sync) {
						Monitor.Pulse(item2.sync);
					}
				}
			}
		}

		public void PulseAll() {
			if (!HasLock) { throw new InvalidOperationException("Aquire lock first"); }

			(TaskCompletionSource<object> async, object sync)[] items;
			lock (_syncRoot) {
				items = _queue.ToArray();
				_queue.Clear();
			}

			for (int i = 0; i < items.Length; i++) {
				var item = items[i];
				if (item.async != null) {
					item.async.TrySetResult(null);
				}
				if (item.sync != null) {
					lock (item.sync) {
						Monitor.Pulse(item.sync);
					}
				}
			}
		}

		public bool Wait() {
			if (!HasLock) { throw new InvalidOperationException("Aquire lock first"); }
			var threadId = _threadId.Value.Value;
			var lockCount = _lockCount;

			object lockObject = new object();
			lock (_syncRoot) {
				_queue.Add((null, lockObject));
			}

			lock (lockObject) {
				Release();
				Monitor.Wait(lockObject);
			}
			ReaquireLock(threadId, lockCount);
			return true;
		}

		public bool Wait(TimeSpan timeout) {
			if (!HasLock) { throw new InvalidOperationException("Aquire lock first"); }
			var threadId = _threadId.Value.Value;
			var lockCount = _lockCount;

			object lockObject = new object();
			lock (_syncRoot) {
				_queue.Add((null, lockObject));
			}

			bool result;
			lock (lockObject) {
				Release();
				result = Monitor.Wait(lockObject, timeout);
			}
			if (!result) {
				lock (_syncRoot) {
					for (int i = _queue.Count - 1; i >= 0; i--) {
						var item = _queue[i];
						if (item.sync == lockObject) {
							_queue.RemoveAt(i);
							break;
						}
					}
				}
			}
			ReaquireLock(threadId, lockCount);
			return result;
		}

		public Task<bool> WaitAsync() {
			if (!HasLock) { throw new InvalidOperationException("Aquire lock first"); }
			var threadId = _threadId.Value.Value;
			var lockCount = _lockCount;

			var taskCompletion = new TaskCompletionSource<object>();
			lock (_syncRoot) {
				_queue.Add((taskCompletion, null));
			}

			var result = WaitInternal(taskCompletion.Task, threadId, lockCount);

			Release();
			return result;
		}

		public Task<bool> WaitAsync(TimeSpan timeout) {
			if (!HasLock) { throw new InvalidOperationException("Aquire lock first"); }
			var threadId = _threadId.Value.Value;
			var lockCount = _lockCount;

			var signal = new CancellationTokenSource(timeout);
			var taskCompletion = new TaskCompletionSource<object>();
			lock (_syncRoot) {
				_queue.Add((taskCompletion, null));
			}

			var task = taskCompletion.Task;
			var registration = signal.Token.Register(_ => CancelWait(task), false);
			task.ContinueWith(_ => {
				registration.Dispose();
				signal.Dispose();
			}, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

			var result = WaitInternal(task, threadId, lockCount);

			Release();
			return result;
		}

		private void CancelWait(Task task) {
			(TaskCompletionSource<object> async, object sync)? item2 = null;
			lock (_syncRoot) {
				for (int i = _queue.Count - 1; i >= 0; i--) {
					var item = _queue[i];
					if (item.async.Task == task) {
						item2 = item;
						_queue.RemoveAt(i);
						break;
					}
				}
			}

			if (item2.HasValue) {
				var item = item2.Value;
				if (item.async != null) {
					item.async.TrySetCanceled();
				}
				if (item.sync != null) {
					lock (item.sync) {
						Monitor.Pulse(item.sync);
					}
				}
			}
		}

		private async Task<bool> WaitInternal(Task task, Guid threadId, int lockCount) {
			await Task.WhenAny(task).NoSync();
			var status = task.Status;

			await _semaphore.WaitAsync().NoSync();
			_lockHolder = threadId;
			_lockCount = lockCount;

			if (status == TaskStatus.Canceled) {
				return false;
			}
			else if (status == TaskStatus.RanToCompletion) {
				return true;
			}
			throw new Exception("Unexpected task status: " + status);
		}

		private IDisposable LockAsyncWaitCompleted(Task task, object state) {
			var threadId = (Guid)state;
			if (_lockCount != 0) { throw new Exception("Internal state corruption"); }
			_lockHolder = threadId;
			_lockCount = 1;
			return _releaser;
		}

		private void Release() {
			if (_lockCount == 0) { throw new Exception("Internal state corruption"); }
			_lockCount = 0;
			_lockHolder = null;
			_semaphore.Release();
		}

		private void ReaquireLock(Guid threadId, int lockCount) {
			_semaphore.Wait();
			_lockHolder = threadId;
			_lockCount = lockCount;
		}

		private void FinalRelease() {
			if (_lockCount != 1) { throw new Exception("Internal state corruption"); }
			_lockHolder = null;
			_lockCount = 0;
			_semaphore.Release();
		}

		private void ReentrantRelease() {
			if (_lockCount <= 1) { throw new Exception("Internal state corruption"); }
			_lockCount--;
		}

		private sealed class ReentrantReleaser : IDisposable {
			private readonly AsyncMonitor _toRelease;

			internal ReentrantReleaser(AsyncMonitor toRelease) {
				_toRelease = toRelease;
			}

			public void Dispose() {
				_toRelease.ReentrantRelease();
			}
		}

		private sealed class Releaser : IDisposable {
			private readonly AsyncMonitor _toRelease;

			internal Releaser(AsyncMonitor toRelease) {
				_toRelease = toRelease;
			}

			public void Dispose() {
				_toRelease.FinalRelease();
			}
		}
	}
}
