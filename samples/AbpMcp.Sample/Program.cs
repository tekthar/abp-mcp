using AbpMcp.Sample;

var builder = WebApplication.CreateBuilder(args);

// The standard ABP host: Autofac runs ABP's OnRegistered hooks, which collect permission definition providers and
// put the authorization, unit-of-work, validation and auditing interceptors on application services.
builder.Host.UseAutofac();

await builder.Services.AddApplicationAsync<LibrarySampleModule>().ConfigureAwait(false);

var app = builder.Build();

await app.InitializeApplicationAsync().ConfigureAwait(false);

app.Run();
