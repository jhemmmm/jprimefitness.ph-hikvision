using HikVision.Models;
using HikVision.Services;
using Serilog;

namespace HikVision.Handlers;

public static class PersonDeleteHandler
{
    public static async Task<PersonResponse> Handle(
        PersonRequest request,
        HikVisionIsapiClient hikVisionIsapiClient)
    {
        var logger = Log.ForContext(typeof(PersonDeleteHandler));

        try
        {
            logger.Information("Handling person delete for employee {EmployeeNo}", request.EmployeeNo);

            var (status, message, httpStatusCode, rawResponse) =
                await hikVisionIsapiClient.DeletePersonAsync(request.EmployeeNo);

            return new PersonResponse
            {
                Status = status,
                Message = message,
                HttpStatusCode = httpStatusCode,
                EmployeeNo = request.EmployeeNo,
                RawResponse = rawResponse
            };
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Unexpected error in person delete handler");

            return new PersonResponse
            {
                Status = "failed",
                Message = $"Unexpected error: {ex.Message}",
                EmployeeNo = request.EmployeeNo
            };
        }
    }
}
