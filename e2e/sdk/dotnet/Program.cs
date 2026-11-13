using TerraScale.E2E;

using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
return Environment.GetEnvironmentVariable("E2E_SESSION_FAULT") is not null
    ? await SessionFault.RunAsync(deadline.Token)
    : await SdkRunner.RunAsync(deadline.Token);
