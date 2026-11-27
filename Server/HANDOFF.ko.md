# 계정 전환 인계 — Backend M1

작성일: 2026-11-27 (Asia/Seoul). 사용자 승인 범위는 독립 백엔드의 첫 완료 지점인 로그인 → 창고 조회 → 이동/장착 → 즉시 저장 → 재로그인 복원과 관련 검증이다.

## 작업 위치와 현재 상태

- 저장소: `C:\Users\user1\Documents\Github\DeltaForce_CPP`
- 원격: `https://github.com/immigration2000/DeltaForce_CPP`
- 작업 브랜치: `codex/server-lobby-mvp`
- 기존 UE의 Source/Content는 수정하지 않았다. 별도 Server 솔루션과 Contracts를 추가했다.
- 기존 `Grid-based-Inventory-System`은 정의 JSON을 읽기만 했다. 기존 코드/DB를 수정하거나 기존 PlayerJson을 가져오지 않았다.
- 서버 API와 DB 기준으로 M1을 구현했다. 실제 UE 로그인 화면·ItemManager와 연결하는 작업은 남아 있다.

## 바로 이어받는 방법

1. 위 저장소에서 `git status`, `git log -1`로 상태를 확인한다. 로컬 커밋이 있어도 원격 push 여부를 별도로 확인한다.
2. `Server/README.ko.md`, `Contracts/README.md`와 OpenAPI를 읽는다.
3. 저장소 루트에서 `.\Server\Test-Server.ps1`로 동일한 검증을 실행한다.
4. 개발 실행은 `.\Server\Start-Server.ps1 -SeedDemoAccounts`다. 사용자 요청에 따라 `demo`, `demo2` 비밀번호를 `1234`로 통일했다. 없는 계정은 생성하고 기존 demo 계정은 비밀번호만 갱신한다. 아이템·장착·Revision은 유지한다.
5. 서버 주소는 `http://127.0.0.1:5099`, 기본 DB는 별도 `DeltaForce_Development`다. 통합 검사는 별도의 무작위 DB를 만들고 제거한다.

설치된 시스템 dotnet은 9.0.317이므로 이 PC에서는 실행 스크립트가 UE 5.8 포함 SDK 10.0.203을 선택한다. EF Core/SQL provider는 10.0.15다. 이 PC의 `.\SQLEXPRESS`는 통합 인증으로 접근 가능한 SQL Server Express다. 다른 계정/PC는 해당 Windows 사용자의 SQL 권한을 확인한다.

## 구현한 기능

- 새 v1 로그인·로그아웃과 본인 인벤토리/정의 조회.
- Move, Rotate, Equip, Unequip, Swap, Merge, Split의 서버 검증과 Delta.
- 좌표·회전·경계·점유·카테고리·슬롯·중첩 순환·스택·소유권/접근 검사.
- 장착 가방과 내용물 유지, 보관 중 가방 내부 조작 차단.
- 요청 ID 멱등성과 payload 충돌, Revision 검증, 결과 조회.
- 실제 MSSQL 트랜잭션, 플레이어 단위 DB 직렬화, 복합 FK/장착 슬롯 유일 인덱스, merge GUID tombstone.
- EF Migration, 실행/테스트 스크립트, OpenAPI, Postman, 공통 배치 테스트 벡터.
- 원본 UE 정의 282개·레이아웃 46개를 독립 manifest로 반입. 현재 SHA-256은 정의 파일에서 계산한다.

## 검증 증거

전체 빌드 경고 0/오류 0. xUnit 10개 통과(공통 JSON 배치 벡터 6개 포함). 실제 SQL/HTTP 검사 79개 통과(기존 demo 비밀번호 변경 및 인벤토리 보존 포함). Postman은 실제 서버에 6개 요청/8개 assertion 통과. OpenAPI syntax lint 문제 없음.

두 API 프로세스가 같은 플레이어에 쓰는 조건에서 동일 키 8개 동시 요청은 한 번만 반영됐다. 서로 다른 키와 같은 Revision은 한 요청만 성공했다. 조회/변경 경합에서 Snapshot은 변경 전/후의 완전한 상태였다. SQL trigger로 OperationRecord insert를 실패시켜 아이템·위치·Revision·기록 전체 롤백을 확인했다. 서버 프로세스 강제 종료 후 새 로그인에서 동일 상태/과거 요청 결과가 유지됐다. 장착 슬롯 두 개의 원자적 교환과 유일 인덱스도 실제 SQL에서 검증했다.

검사는 독립 임시 DB와 프로세스를 사용하고 종료 시 정리한다. UE 빌드·PIE·화면 연동, 레이드 플레이, 성능 부하 검증은 실행하지 않았다. 단위/통합 테스트 통과를 해당 기능 완료 증거로 확대하지 않는다.

## 유지해야 할 결정

- 백엔드는 C#/.NET, 이후 UE 로직은 C++로 이전한다. C# 코드를 C++에 직접 공유하지 않고 계약/manifest/벡터를 공유한다.
- 같은 저장소, 별도 Server 솔루션. 실행 API는 하나이며 마이크로서비스/Redis/JWT를 선행 도입하지 않는다.
- 레거시 전체 스냅샷 업로드 경로를 새 서버에 만들지 않는다. 로그아웃은 저장 없이 토큰만 폐기한다.
- ItemId·DefinitionId·Slot.* 태그 철자를 유지한다. Revision은 문자열이다.
- Snapshot과 쓰기 모두 플레이어 행의 UPDLOCK/HOLDLOCK을 잡아 현재 M1 범위에서 일관성을 보장한다.
- 멱등 기록은 변경과 같은 transaction에 저장하며 현재 삭제/TTL이 없다.
- 메모리 세션은 재시작 시 만료되고 API 프로세스끼리 공유되지 않는다. 테스트는 각 프로세스에서 별도로 로그인하여 DB 동시성을 검증했다.
- manifest의 내구도 0은 실제 게임 수치가 아니라 미구현 표시다. 안전금고/키홀더 장착은 정의별 매핑 확정 전 차단한다.

## 다음 작업

1. UE C++ HTTP transport와 v1 DTO를 만들고 BaseGI에는 세션만 유지한다.
2. Client ItemManager에 Snapshot/Delta/Revision 적용, Pending·오류·재동기화 경로를 붙인다. 서버 확정 전 로컬 상태를 영구 상태로 저장하지 않는다.
3. 공통 벡터를 UE C++ 테스트에서도 실행하고 실제 로비 화면에서 로그인·이동·장착·재로그인을 검증한다.
4. 레이드 도입 전 custody/escrow/claim, ActiveEntry, 서버 신원/epoch, 티켓과 정산 스키마를 추가한다. 현재 플레이어 복합 FK의 소유권 이전 방식도 설계한다.
5. 원본 PlayerJson 이관이 필요하면 별도 importer·백업·동등성 검증을 설계한다.

레이드 입장/정산·서버 간 인증·결과 outbox·사망 보호·반환 holding은 미구현이다. 해당 정책은 기존 Notion 공통 계약과 Backend SDD를 기준으로 후속 결정한다. API를 다중 인스턴스로 배포하려면 세션 공유/라우팅, DB migration 운영, HTTPS 및 공개 계정 발급 정책도 추가한다.

## 기획 기준

- 전체 아키텍처: https://app.notion.com/p/42506cd277668178bf94f818f5c22aee
- 공통 계약: https://app.notion.com/p/42506cd277668149a136f74564b23ed2
- Backend PRD: https://app.notion.com/p/42506cd2776681a0a80ecd3c7f59d354
- Backend SDD: https://app.notion.com/p/42506cd2776681fd893cdd2cccd89320

사용자가 작업 중 5시간 사용률 95%가 되면 Notion에 인계 정보를 저장하도록 요청했다. 이번 작업에서는 사용량을 주기적으로 확인하며 완료 시에도 인계를 저장한다. 개발 demo 비밀번호 `1234`는 사용자가 명시한 공통 테스트 값이다. 실제 토큰·개인 SQL 접속 비밀은 기록하지 않는다.
