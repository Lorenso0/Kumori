using Xunit;

// These tests use isolated database files, but their cleanup must call
// SqliteConnection.ClearAllPools so Windows releases those files. Running
// test classes concurrently can therefore dispose another class's live pool.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
