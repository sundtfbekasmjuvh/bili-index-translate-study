using Xunit;

// UI tests drive the real desktop (keyboard, mouse, foreground window): never run two at once.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
