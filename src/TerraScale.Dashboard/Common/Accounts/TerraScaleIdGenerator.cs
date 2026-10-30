using NUlid;
using NUlid.Rng;

namespace TerraScale.Dashboard.Common.Accounts
{
    // Cryptographic ULIDs remain monotonic within this service, including across backward clock adjustments.
    public sealed class TerraScaleIdGenerator
    {
        private readonly object gate = new();
        private readonly MonotonicUlidRng random = new(new CSUlidRng());
        private readonly TimeProvider timeProvider;
        private DateTimeOffset? lastTime;
        public TerraScaleIdGenerator() : this(TimeProvider.System)
        {
        }

        internal TerraScaleIdGenerator(TimeProvider timeProvider)
        {
            this.timeProvider = timeProvider;
        }

        public string NewId()
        {
            lock (gate)
            {
                var now = timeProvider.GetUtcNow();
                if (lastTime is { } previous && now < previous)
                {
                    now = previous;
                }

                var id = Ulid.NewUlid(now, random).ToString();
                lastTime = now;
                return id;
            }
        }
    }
}
