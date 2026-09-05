using Xunit;

// The live-PTY test forks; forking while sibling tests run threads is the
// textbook fork/threads race. One collection at a time keeps runs deterministic.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
