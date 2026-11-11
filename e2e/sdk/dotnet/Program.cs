using TerraScale.E2E;

using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
return await SdkRunner.RunAsync(deadline.Token);
