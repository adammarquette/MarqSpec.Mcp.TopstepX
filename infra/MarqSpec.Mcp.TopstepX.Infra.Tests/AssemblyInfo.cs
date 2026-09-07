// SERIAL, deliberately. Every construct call here goes through the jsii runtime, which starts one Node process
// per test host and, on first use, extracts the CDK's package tarballs into a temp directory. xUnit's default
// runs test classes in parallel, so two class fixtures initialising at once race on that extraction --
// measured on this suite: `IOException: The process cannot access the file '...aws-cdk-lib-2.268.0.tgz'
// because it is being used by another process` in every test of the second class to start, and one run that
// hung a single test for four and a half minutes. The kernel is one process either way, so parallel classes
// bought nothing to give up.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
