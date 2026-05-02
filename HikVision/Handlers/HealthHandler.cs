using HikVision.Models;

namespace HikVision.Handlers;

public static class HealthHandler
{
    public static HealthResponse Handle()
    {
        return new HealthResponse();
    }
}
