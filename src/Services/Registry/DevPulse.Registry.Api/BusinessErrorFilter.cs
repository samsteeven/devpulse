using DevPulse.Registry.Application;
using DevPulse.Registry.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DevPulse.Registry.Api;

/// <summary>Une règle métier refusée est une réponse normale : elle ne doit pas être journalisée comme une erreur du service.</summary>
public sealed class BusinessErrorFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        int? status = context.Exception switch
        {
            DomainException => StatusCodes.Status400BadRequest,
            DuplicateServiceException => StatusCodes.Status409Conflict,
            _ => null,
        };
        if (status is null) return;

        context.Result = new ObjectResult(new ProblemDetails { Status = status, Title = context.Exception.Message }) { StatusCode = status };
        context.ExceptionHandled = true;
    }
}
