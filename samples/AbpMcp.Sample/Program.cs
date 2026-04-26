using AbpMcp.Sample;

var builder = WebApplication.CreateBuilder(args);

await builder.Services.AddApplicationAsync<LibrarySampleModule>().ConfigureAwait(false);

var app = builder.Build();

await app.InitializeApplicationAsync().ConfigureAwait(false);

app.Run();
