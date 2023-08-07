# 1.0.0

* Initial release

# 2.0.0

* Replaced BinaryFormatter with Json for localization cache

This breaking change was introduced due to Microsoft having announced that the BinaryFormatter serialization methods are now obsolete and will be deprecated for security reasons:

https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/5.0/binaryformatter-serialization-obsolete

Hence the increase in major version number.

# 2.0.1

* Increased range for maxRefreshResponseTimeMilliseconds

# 2.1.0

* Allow returning translation key instead of `null` as fallback value when entry is not found
