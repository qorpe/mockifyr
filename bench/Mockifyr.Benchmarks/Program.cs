using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Mockifyr.Benchmarks;

// Two modes on purpose. The default is a full BenchmarkDotNet run for publishing numbers; `--quick`
// trades precision for a run short enough to sit in CI, where the job exists to make a regression
// visible rather than to produce a citable figure.
var quick = args.Contains("--quick");
var config = quick
    ? DefaultConfig.Instance.AddJob(Job.Dry.WithIterationCount(3).WithWarmupCount(1))
    : DefaultConfig.Instance;

BenchmarkRunner.Run<EngineBenchmarks>(config, quick ? args.Where(a => a != "--quick").ToArray() : args);
