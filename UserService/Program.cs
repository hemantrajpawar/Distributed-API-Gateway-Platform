var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();


// GET /users
app.MapGet("/users", () =>
{
    var instance =
        Environment.GetEnvironmentVariable("INSTANCE_NAME")
        ?? "user-service-1";

    return Results.Ok(new
    {
        service = "User Service",
        instance = instance,
        message = "Hello from User Service"
    });
});

// GET /users/{id}
app.MapGet("/users/{id}", (int id) =>
{
    var instance =
        Environment.GetEnvironmentVariable("INSTANCE_NAME")
        ?? "user-service-1";

    return Results.Ok(new
    {
        service = "User Service",
        instance = instance,
        userId = id,
        name = $"User {id}"
    });
});

// POST /users
app.MapPost("/users", async (HttpRequest request) =>
{
    var body = await request.ReadFromJsonAsync<User>();

    return Results.Ok(new
    {
        service = "User Service",
        message = "User received successfully",
        user = body
    });
});


// Health check
app.MapGet("/health", () =>
{
    return Results.Ok(new
    {
        status = "Healthy",
        service = "User Service"
    });
});


app.Run();


record User(string Name, string Email);