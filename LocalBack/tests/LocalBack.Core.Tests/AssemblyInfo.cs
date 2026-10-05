// The key store is process-wide (as in the app), so tests that clear or fill it must not overlap.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
