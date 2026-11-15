using Microsoft.AspNetCore.Http;
using OrderProcessing.BuildingBlocks.Domain;

namespace OrderProcessing.BuildingBlocks.Infrastructure;

public static class ResultHttpExtensions
{
    /// <summary>Maps an <see cref="Error"/> to an RFC 9457 problem details response.</summary>
    public static IResult ToProblem(this Error error)
    {
        var status = error.Type switch
        {
            ErrorType.Validation => StatusCodes.Status400BadRequest,
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            ErrorType.Unavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError,
        };

        return Results.Problem(
            statusCode: status,
            title: error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = error.Code });
    }
}
