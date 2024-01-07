using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace Application.Shared.ErrorResult
{
    public class ResultEntity
    {
        public bool Success { get; protected set; }
        public bool Failure => !Success;

    }
    public abstract class ResultEntity<T> : ResultEntity
    {
        private T _data;
        protected ResultEntity(T data)
        {
            _data = data;
        }


        public T Data
        {
            get => Success ? _data : throw new Exception($"You can't access .{nameof(Data)} when .{nameof(Success)} is false");
            set => _data = value;
        }


    }

    public class SuccessResult2 : ResultEntity
    {
        public SuccessResult2()
        {
            Success = true;
        }
    }

    public class SuccessResult2<T> : ResultEntity<T>
    {
        public SuccessResult2(T data) : base(data)
        {
            Success = true;
        }
    }

    public class ErrorResult : ResultEntity
    {
        public string Message { get; }
        public IReadOnlyCollection<Application.Helpers.Error> Errors { get; }
        public ErrorResult(string message) : this(message, Array.Empty<Application.Helpers.Error>())
        {
            Success = false;
        }
        public ErrorResult(string message, IReadOnlyCollection<Application.Helpers.Error> errors)
        {
            Message = message;
            Errors = errors;
            Success = false;
        }
    }

    public class ErrorResult<T> : ResultEntity<T>
    {
        public ErrorResult(string message) : this(message, Array.Empty<Application.Helpers.Error>())
        {
        }

        public ErrorResult(string message, IReadOnlyCollection<Application.Helpers.Error> errors) : base(default)
        {
            Message = message;
            Success = false;
            Errors = errors ?? Array.Empty<Application.Helpers.Error>();
        }

        public string Message { get; set; }
        public IReadOnlyCollection<Application.Helpers.Error> Errors { get; }
    }
    public class ValidationErrorResult : ErrorResult
    {
        public ValidationErrorResult(string message) : base(message)
        {
        }

        public ValidationErrorResult(string message, IReadOnlyCollection<ValidationError> errors) : base(message, errors)
        {
        }
    }

    public class ValidationError : Application.Helpers.Error
    {
        public ValidationError(string propertyName, string details) : base(null, details)
        {
            PropertyName = propertyName;
        }

        public string PropertyName { get; }
    }


    // public Result ValidateMyData(MyData data)
    // {
    //     if(data.IsValid)
    //     {
    //         return new SuccessResult();
    //     }
    //     return new ValidationErrorResult("Error when validating...", data.Errors.Select(x => new ValidationError(x.PropertyName, x.Message)))
    // }

    public class HttpErrorResult : ErrorResult
    {
        public HttpStatusCode StatusCode { get; }

        public HttpErrorResult(string message, HttpStatusCode statusCode) : base(message)
        {
            StatusCode = statusCode;
        }

        public HttpErrorResult(string message, IReadOnlyCollection<Application.Helpers.Error> errors, HttpStatusCode statusCode) : base(message, errors)
        {
            StatusCode = statusCode;
        }
    }

}