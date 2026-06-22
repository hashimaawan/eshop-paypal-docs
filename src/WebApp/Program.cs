using eShop.WebApp.Components;
using eShop.ServiceDefaults;
using eShop.WebApp.PayPal;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// Session is used to persist the PayPal order id between the time an order is
// created in /paypal/pay and when the shopper is redirected back to checkout.
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

builder.AddApplicationServices();

// PayPal Server SDK based checkout (UC1: create order + authorize).
builder.AddPayPalCheckout();

var app = builder.Build();

app.MapDefaultEndpoints();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseAntiforgery();

app.UseHttpsRedirection();

app.UseStaticFiles();

app.UseSession();

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// PayPal OAuth endpoints (Log in with PayPal)
app.MapPayPalEndpoints();

app.MapForwarder("/product-images/{id}", "https+http://catalog-api", "/api/catalog/items/{id}/pic");

app.Run();
