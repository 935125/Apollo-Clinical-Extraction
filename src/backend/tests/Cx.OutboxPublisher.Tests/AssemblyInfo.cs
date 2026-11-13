using System.Reflection;
using Xunit;

// Disable parallel test execution across outbox integration test classes to prevent race conditions on shared emr.extraction_outbox table
[assembly: CollectionBehavior(DisableTestParallelization = true)]

