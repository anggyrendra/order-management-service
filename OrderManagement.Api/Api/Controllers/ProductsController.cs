using Microsoft.AspNetCore.Mvc;
using OrderManagement.Application.Interfaces;

namespace OrderManagement.Api.Controllers;

/// <summary>
/// Read-only product endpoints for the prototype. Provided mainly so the API is
/// immediately explorable (and so tests can verify stock after operations).
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class ProductsController : ControllerBase
{
    private readonly IProductService _products;

    public ProductsController(IProductService products) => _products = products;

    [HttpGet]
    [ProducesResponseType(typeof(List<ProductResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        await _products.EnsureProductsSeededAsync(cancellationToken);
        var list = await _products.ListProductsAsync(cancellationToken);
        return Ok(list);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ProductResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        await _products.EnsureProductsSeededAsync(cancellationToken);
        var product = await _products.GetProductAsync(id, cancellationToken);
        if (product == null) return NotFound();
        return Ok(product);
    }
}
