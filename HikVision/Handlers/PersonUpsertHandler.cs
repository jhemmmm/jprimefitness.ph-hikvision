using HikVision.Models;
using HikVision.Services;
using Serilog;

namespace HikVision.Handlers;

public static class PersonUpsertHandler
{
    public static async Task<PersonResponse> Handle(
        PersonUpsertRequest request,
        HikVisionIsapiClient hikVisionIsapiClient)
    {
        var logger = Log.ForContext(typeof(PersonUpsertHandler));

        try
        {
            logger.Information("Handling person upsert for employee {EmployeeNo}", request.EmployeeNo);

            var (status, message, httpStatusCode, rawResponse) = await hikVisionIsapiClient.UpsertPersonAsync(
                request.EmployeeNo,
                request.Name,
                request.UserType,
                request.ValidityEnabled,
                request.ValidFrom,
                request.ValidTo,
                request.CheckUser,
                request.CloseDelayEnabled,
                request.NumberOfFingerprints);

            return new PersonResponse
            {
                Status = status,
                Message = message,
                HttpStatusCode = httpStatusCode,
                EmployeeNo = request.EmployeeNo,
                Name = request.Name,
                RawResponse = rawResponse
            };
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Unexpected error in person upsert handler");

            return new PersonResponse
            {
                Status = "failed",
                Message = $"Unexpected error: {ex.Message}",
                EmployeeNo = request.EmployeeNo,
                Name = request.Name
            };
        }
    }
}
