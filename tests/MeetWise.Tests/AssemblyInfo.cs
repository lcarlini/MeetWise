using Xunit;

// Timing-sensitive tests (real-time factor) must not compete for CPU with model-heavy tests.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
