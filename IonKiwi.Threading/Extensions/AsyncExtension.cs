using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace IonKiwi.Extensions {
	public static class AsyncExtension {
		public static System.Runtime.CompilerServices.ConfiguredTaskAwaitable NoSync(this Task task) => task.ConfigureAwait(false);
		public static System.Runtime.CompilerServices.ConfiguredTaskAwaitable<T> NoSync<T>(this Task<T> task) => task.ConfigureAwait(false);
#if !NETSTANDARD2_0
		public static System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable NoSync(this ValueTask task) => task.ConfigureAwait(false);
		public static System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable<T> NoSync<T>(this ValueTask<T> task) => task.ConfigureAwait(false);
#endif
	}
}
