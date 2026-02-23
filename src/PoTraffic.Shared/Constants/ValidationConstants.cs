namespace PoTraffic.Shared.Constants;

public static class ValidationConstants
{
    public const int EmailMaxLength = 320;
    public const int AddressMaxLength = 500;
    public const int LocaleMaxLength = 50;
    public const string EmailRegex = @"^[^@\s]+@[^@\s]+\.[^@\s]+$";
}
