var builder = WebApplication.CreateBuilder(args);

builder.AddBasicServiceDefaults();
builder.AddApplicationServices();

builder.Services.AddGrpc();

var app = builder.Build();

// Enable authentication/authorization so Basket gRPC methods can see the authenticated user
app.UseAuthentication();
app.UseAuthorization();

app.MapDefaultEndpoints();

app.MapGrpcService<BasketService>();

app.Run();
