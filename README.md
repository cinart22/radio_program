Online radio listening and recording cli app for windows and linux. Some radio stations could not be listened, due to georestrictions. Radio station apis can be manually added to the list (Program.cs Line 55). Recordings are saved to 
/recordings directory. Program mostly works as intended. However, network status change causes bugs. If such bug encountered, rerunning the application should solve the issue and you can enjoy the app after network status change.

Dependincies

.NET 9.0 SDK 

FFmpeg;

For arch: sudo pacman -S ffmpeg 

For windows: visit https://www.ffmpeg.org/download.html

OpenAL;

For arch: sudo pacman -S openal 

For windows, it is included with OpenTK, do not need to download anything.

#Running the app

dotnet add package FFmpeg.AutoGen \
dotnet add package OpenTK\
dotnet build\
dotnet run

*On other linux distrubutions and on mac, change the library path on the 'Program.cs Line 204' to your distribution default ffmpeg library path.
