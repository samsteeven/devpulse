using DevPulse.Identity.Application;
using DevPulse.Identity.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DevPulse.Identity.Api;

/// <summary>Une règle métier refusée est une réponse normale : elle ne doit pas être journalisée comme une erreur du service.</summary>
public sealed class BusinessErrorFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is PasswordRejectedException rejected)
        {
            var errors = new Dictionary<string, string[]> { ["Password"] = [.. rejected.Errors] };
            context.Result = new BadRequestObjectResult(new ValidationProblemDetails(errors));
            context.ExceptionHandled = true;
            return;
        }

        int? status = context.Exception switch
        {
            DomainException => StatusCodes.Status400BadRequest,
            ForbiddenException => StatusCodes.Status403Forbidden,
            ConflictException => StatusCodes.Status409Conflict,
            _ => null,
        };
        if (status is null) return;

        context.Result = new ObjectResult(new ProblemDetails { Status = status, Title = context.Exception.Message }) { StatusCode = status };
        context.ExceptionHandled = true;
    }
}
