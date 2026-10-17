using Nodus.Api;

var app = NodusApplication.Build(args);
if (await NodusApplication.TryRunCommandAsync(app, args)) return;
await app.RunAsync();
