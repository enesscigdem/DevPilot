namespace DevPilot.Application.GitProviders;

public class GitProviderResult<T>
{
    public bool IsSuccess { get; set; }

    public string? ErrorMessage { get; set; }

    public T? Data { get; set; }

    public static GitProviderResult<T> Success(T data)
    {
        return new GitProviderResult<T>
        {
            IsSuccess = true,
            Data = data,
        };
    }

    public static GitProviderResult<T> Failure(string errorMessage)
    {
        return new GitProviderResult<T>
        {
            IsSuccess = false,
            ErrorMessage = errorMessage,
        };
    }
}
