using Nodus.Api;

var app = ApplicationHost.Build(args);
if (await ApplicationHost.TryRunCommandAsync(app, args)) return;
await app.RunAsync();
