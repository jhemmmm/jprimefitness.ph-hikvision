using HikVision.Models;
using HikVision.Services;
using Serilog;

namespace HikVision.Handlers;

public static class PersonSearchHandler
{
    public static async Task<PersonResponse> Handle(
        PersonRequest request,
        HikVisionIsapiClient hikVisionIsapiClient)
    {
        var logger = Log.ForContext(typeof(PersonSearchHandler));

        try
        {
            logger.Information("Handling person search for employee {EmployeeNo}", request.EmployeeNo);

            var (status, message, httpStatusCode, userInfo, rawResponse) =
                await hikVisionIsapiClient.SearchPersonAsync(request.EmployeeNo);

            return new PersonResponse
            {
                Status = status,
                Message = message,
                HttpStatusCode = httpStatusCode,
                EmployeeNo = request.EmployeeNo,
                Name = userInfo?["name"]?.ToString(),
                UserInfo = userInfo,
                RawResponse = rawResponse
            };
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Unexpected error in person search handler");

            return new PersonResponse
            {
                Status = "failed",
                Message = $"Unexpected error: {ex.Message}",
                EmployeeNo = request.EmployeeNo
            };
        }
    }
}
