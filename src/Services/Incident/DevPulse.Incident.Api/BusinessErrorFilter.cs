using DevPulse.Incident.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DevPulse.Incident.Api;

/// <summary>Une règle métier refusée est une réponse normale : elle ne doit pas être journalisée comme une erreur du service.</summary>
public sealed class BusinessErrorFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        int? status = context.Exception switch
        {
            InvalidTransitionException => StatusCodes.Status409Conflict,
            DomainException => StatusCodes.Status400BadRequest,
            _ => null,
        };
        if (status is null) return;

        context.Result = new ObjectResult(new ProblemDetails { Status = status, Title = context.Exception.Message }) { StatusCode = status };
        context.ExceptionHandled = true;
    }
}
