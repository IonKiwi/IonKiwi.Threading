using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace IonKiwi.Threading {
	// based on AsyncLock by Stephen Toub 
	// http://www.hanselman.com/blog/ComparingTwoTechniquesInNETAsynchronousCoordinationPrimitives.aspx
	public sealed class AsyncLock {
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

		public AsyncLock() {
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

		internal int LockCount => _lockCount;

		internal bool HasLock {
			get {
				var l = _lockHolder;
				return l.HasValue && l.Value == _threadId.Value;
			}
		}

		public IDisposable LockSync() {
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

#if NETSTANDARD2_0
		public Task<IDisposable> LockAsync() {
#else
		public ValueTask<IDisposable> LockAsync() {
#endif
			// it's very important that this method is not async
			// so re-entry works correctly (set AsyncLocal value must not be inside a async state machine)

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

		private IDisposable LockAsyncWaitCompleted(Task task, object state) {
			var threadId = (Guid)state;
			if (_lockCount != 0) { throw new Exception("Internal state corruption"); }
			_lockHolder = threadId;
			_lockCount = 1;
			return _releaser;
		}

		private void Release() {
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
			private readonly AsyncLock _toRelease;

			internal ReentrantReleaser(AsyncLock toRelease) {
				_toRelease = toRelease;
			}

			public void Dispose() {
				_toRelease.ReentrantRelease();
			}
		}

		private sealed class Releaser : IDisposable {
			private readonly AsyncLock _toRelease;

			internal Releaser(AsyncLock toRelease) {
				_toRelease = toRelease;
			}

			public void Dispose() {
				_toRelease.Release();
			}
		}
	}
}
