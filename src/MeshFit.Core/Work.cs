using System;
using System.Threading;
using System.Threading.Tasks;

namespace MeshFit.Core
{
    /// <summary>Shared limits for parallel work: always leave a core for the host application's UI thread.</summary>
    public static class Work
    {
        public static int MaxThreads => Math.Max(1, Environment.ProcessorCount - 1);

        public static ParallelOptions Options(CancellationToken ct = default) =>
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = MaxThreads };
    }
}
