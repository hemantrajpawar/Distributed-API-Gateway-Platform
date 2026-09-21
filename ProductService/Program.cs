var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/products", () =>
{
    return Results.Ok(new
    {
        service = "Product Service",
        instance = "product-service-1",
        products = new[]
        {
            new { id = 1, name = "Laptop", price = 70000 },
            new { id = 2, name = "Phone", price = 30000 },
            new { id = 3, name = "Keyboard", price = 3000 }
        }
    });
});

app.MapGet("/products/{id}", (int id) =>
{
    return Results.Ok(new
    {
        service = "Product Service",
        productId = id,
        name = $"Product {id}"
    });
});

app.MapGet("/health", () =>
{
    return Results.Ok(new
    {
        status = "Healthy",
        service = "Product Service"
    });
});

app.Run();