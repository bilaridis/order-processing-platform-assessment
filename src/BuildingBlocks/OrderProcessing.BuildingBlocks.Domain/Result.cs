using System.Diagnostics.CodeAnalysis;

namespace OrderProcessing.BuildingBlocks.Domain;

public enum ErrorType
{
    Validation,
    NotFound,
    Conflict,
    /// <summary>A dependency is temporarily unavailable; the caller may retry.</summary>
    Unavailable,
    Failure,
}

/// <summary>
/// An expected, business-meaningful failure. Exceptions are reserved for bugs and broken invariants.
/// <see cref="Code"/> is a stable, machine-readable identifier that is part of the public API contract.
/// </summary>
public sealed record Error(string Code, string Message, ErrorType Type = ErrorType.Failure)
{
    public static Error Validation(string code, string message) => new(code, message, ErrorType.Validation);
    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);
    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);
    public static Error Unavailable(string code, string message) => new(code, message, ErrorType.Unavailable);
}

public class Result
{
    protected Result(Error? error) => Error = error;

    public Error? Error { get; }

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    [MemberNotNullWhen(true, nameof(Error))]
    public bool IsFailure => Error is not null;

    public static Result Success() => new(null);

    public static Result Failure(Error error) => new(error);

    public static Result<T> Success<T>(T value) => new(value, null);

    public static Result<T> Failure<T>(Error error) => new(default, error);

    public static implicit operator Result(Error error) => Failure(error);
}

public sealed class Result<T> : Result
{
    private readonly T? _value;

    internal Result(T? value, Error? error)
        : base(error) => _value = value;

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Cannot read the value of a failed result ({Error.Code}).");

    public static implicit operator Result<T>(T value) => new(value, null);

    public static implicit operator Result<T>(Error error) => new(default, error);
}
