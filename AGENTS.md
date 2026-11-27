# Unreal IDE 프로젝트 갱신

- 사용자는 새 C++ 파일이 IDE에 보이지 않는 문제를 직접 .sln 재생성으로 해결했다. 앞으로 에이전트가 IDE 갱신까지 처리한다.
- Unreal C++ 헤더/소스 파일을 추가·삭제·이름 변경·이동했으면 완료 보고 전에 해당 .uproject의 IDE 프로젝트 파일을 재생성한다. 빌드 통과만으로 IDE 반영 완료라고 판단하지 않는다.
- 설치된 엔진의 Build.bat 또는 UnrealBuildTool의 `-Mode=GenerateProjectFiles`를 사용한다. 프로젝트 절대 경로, `-Game -Engine -Automated`를 지정하고 기존 IDE 형식을 유지한다. GenerateProjectFiles.bat이 없는 설치형 엔진은 UnrealBuildTool 경로를 사용한다.
- 재생성 후 Intermediate/ProjectFiles의 .vcxproj와 .vcxproj.filters에 새 파일이 포함되고 삭제·이동 전 경로가 제거됐는지 확인한다.
- 사용자가 이미 재생성하여 등록이 정상인 경우에는 확인 결과를 보고하고 불필요하게 반복하지 않는다.
- 자동 재생성이 불가능하거나 실패하면 그 사실과 이유를 보고하고, `.uproject 우클릭 → Generate Visual Studio project files → IDE에서 솔루션/프로젝트 다시 로드` 절차를 명시한다. 열린 IDE가 별도 재로드를 요구하면 함께 안내한다.
- 사용자 IDE를 강제 종료하거나 저장하지 않은 작업을 버리지 않는다. 기존 Blueprint/소스 변경을 초기화하지 않는다.
- 독립 C# 백엔드는 Server/DeltaForce.Server.slnx를 따로 관리한다. 순수 C# 소스 추가에는 Unreal 솔루션 재생성이 필요하지 않으며, C# 프로젝트 추가·삭제 시에는 해당 솔루션을 갱신한다.
