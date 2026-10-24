using System.Collections.Concurrent;

namespace Contoso.WebApi.Endpoints;

public record Item(Guid Id, string Name);

public record CreateItemRequest(string Name);

/// <summary>In-memory store so the sample runs with no infrastructure. Replace with a real repository.</summary>
public class ItemStore
{
    private readonly ConcurrentDictionary<Guid, Item> _items = new();

    public IEnumerable<Item> All() => _items.Values;

    public Item? Find(Guid id) => _items.GetValueOrDefault(id);

    public Item Add(string name)
    {
        var item = new Item(Guid.NewGuid(), name);
        _items[item.Id] = item;
        return item;
    }
}

public static class ItemEndpoints
{
    public static IEndpointRouteBuilder MapItemEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/items").WithTags("Items");

        group.MapGet("/", (ItemStore store) => store.All());

        group.MapGet("/{id:guid}", (Guid id, ItemStore store) =>
            store.Find(id) is { } item ? Results.Ok(item) : Results.NotFound());

        group.MapPost("/", (CreateItemRequest request, ItemStore store) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(request.Name)] = ["Name is required."]
                });
            }

            var item = store.Add(request.Name);
            return Results.Created($"/api/items/{item.Id}", item);
        });

        return app;
    }
}
