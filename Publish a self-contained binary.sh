dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true   # Windows
dotnet publish -c Release -r linux-x64 --self-contained                              # Linux
dotnet publish -c Release -r osx-x64 --self-contained                                # macOS