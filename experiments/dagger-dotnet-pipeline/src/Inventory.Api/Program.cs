using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http.HttpResults;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<ItemStore>();

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();

// The Dagger pipeline stamps the image with the git commit it was built from (APP_VERSION),
// so after a deploy you can ask the running app which build it is.
app.MapGet("/health", () => TypedResults.Ok(new { status = "healthy" }));
app.MapGet("/version", (IConfiguration config) =>
    TypedResults.Ok(new { version = config["APP_VERSION"] ?? "dev" }));

var items = app.MapGroup("/items");
items.MapGet("/", (ItemStore store) => TypedResults.Ok(store.All()));
items.MapGet("/{id:int}", Results<Ok<Item>, NotFound> (int id, ItemStore store) =>
    store.Find(id) is { } item ? TypedResults.Ok(item) : TypedResults.NotFound());
items.MapPost("/", Results<Created<Item>, ValidationProblem> (NewItem request, ItemStore store) =>
{
    var errors = new Dictionary<string, string[]>();
    if (string.IsNullOrWhiteSpace(request.Name))
    {
        errors["name"] = ["Name is required."];
    }

    if (request.Quantity < 0)
    {
        errors["quantity"] = ["Quantity cannot be negative."];
    }

    if (errors.Count > 0)
    {
        return TypedResults.ValidationProblem(errors);
    }

    var item = store.Add(request.Name!.Trim(), request.Quantity);
    return TypedResults.Created($"/items/{item.Id}", item);
});

app.Run();

public record Item(int Id, string Name, int Quantity);

public record NewItem(string? Name, int Quantity);

internal sealed class ItemStore
{
    private readonly ConcurrentDictionary<int, Item> _items = new();
    private int _nextId;

    public IReadOnlyList<Item> All() => [.. _items.Values.OrderBy(i => i.Id)];

    public Item? Find(int id) => _items.GetValueOrDefault(id);

    public Item Add(string name, int quantity)
    {
        var item = new Item(Interlocked.Increment(ref _nextId), name, quantity);
        _items[item.Id] = item;
        return item;
    }
}
