var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/orders", () =>
{
    return Results.Ok(new
    {
        service = "Order Service",
        instance = "order-service-1",
        orders = new[]
        {
            new { id = 101, status = "Pending" },
            new { id = 102, status = "Completed" }
        }
    });
});

app.MapGet("/orders/{id}", (int id) =>
{
    return Results.Ok(new
    {
        service = "Order Service",
        orderId = id,
        status = "Pending"
    });
});

app.MapGet("/health", () =>
{
    return Results.Ok(new
    {
        status = "Healthy",
        service = "Order Service"
    });
});

app.Run();