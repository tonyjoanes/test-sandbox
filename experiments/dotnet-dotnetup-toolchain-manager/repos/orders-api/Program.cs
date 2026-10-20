using System.Runtime.InteropServices;

// Prints which runtime actually executed this app, so the experiment can show each
// "repository" building and running on its own SDK/runtime from one dotnetup-managed root.
Console.WriteLine($"orders-api: {RuntimeInformation.FrameworkDescription} ({RuntimeInformation.RuntimeIdentifier})");
