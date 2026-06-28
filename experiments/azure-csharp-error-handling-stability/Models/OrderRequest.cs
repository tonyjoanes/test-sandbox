namespace AzureErrorHandlingDemo.Models;

public class OrderRequest
{
    public string UserId { get; set; } = string.Empty;
    public string ProductId { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal Amount { get; set; }
}
