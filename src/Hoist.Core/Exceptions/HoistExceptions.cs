namespace Hoist.Core.Exceptions
{
    /// <summary>
    /// Base for exceptions that carry an HTTP status. Mirrors io.xh.hoist.exception.HttpException /
    /// RoutineException - routine exceptions are expected (bad input, auth) and are logged at debug.
    /// </summary>
    public class HoistException : Exception
    {
        public HoistException(string message, int statusCode = 500, bool isRoutine = false, Exception cause = null)
            : base(message, cause)
        {
            StatusCode = statusCode;
            IsRoutine = isRoutine;
        }

        public int StatusCode { get; }
        public bool IsRoutine { get; }
    }

    /// <summary>The live xh_* schema does not match the EF model. Thrown at startup, so the server does not start.</summary>
    public class HoistSchemaException : HoistException
    {
        public HoistSchemaException(string message) : base(message) { }
    }

    public class NotAuthenticatedException : HoistException
    {
        public NotAuthenticatedException(string message = "Not Authenticated") : base(message, 401, true) { }
    }

    public class NotAuthorizedException : HoistException
    {
        public NotAuthorizedException(string message = "Not Authorized") : base(message, 403, true) { }
    }

    public class NotFoundException : HoistException
    {
        public NotFoundException(string message = "Not Found") : base(message, 404, false) { }
    }

    public class RoutineRuntimeException : HoistException
    {
        public RoutineRuntimeException(string message, Exception cause = null) : base(message, 400, true, cause) { }
    }

    public class DataNotAvailableException : HoistException
    {
        public DataNotAvailableException(string message) : base(message, 400, true) { }
    }

    public class SessionMismatchException : HoistException
    {
        public SessionMismatchException(string message) : base(message, 400, true) { }
    }

    /// <summary>Domain validation failure - messages joined with " | " as in hoist-core's ValidationException.</summary>
    public class ValidationException : HoistException
    {
        public ValidationException(IEnumerable<string> errors) : base(string.Join(" | ", errors), 400, true) { }
        public ValidationException(string error) : base(error, 400, true) { }
    }
}
