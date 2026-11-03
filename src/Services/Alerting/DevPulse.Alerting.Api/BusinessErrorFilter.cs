using DevPulse.Alerting.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DevPulse.Alerting.Api;

/// <summary>Une règle métier refusée est une réponse normale : elle ne doit pas être journalisée comme une erreur du service.</summary>
public sealed class BusinessErrorFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not DomainException) return;

        context.Result = new ObjectResult(new ProblemDetails { Status = StatusCodes.Status400BadRequest, Title = context.Exception.Message })
        {
            StatusCode = StatusCodes.Status400BadRequest,
        };
        context.ExceptionHandled = true;
    }
}
