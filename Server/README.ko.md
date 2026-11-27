# DeltaForce 독립 백엔드 — 로비 M1

UE를 실행하지 않고 로그인 → 창고 조회 → 이동/장착 → 즉시 저장 → 로그아웃/재로그인 복원까지 실행하는 ASP.NET Core 서버다. 클라이언트가 변경 의도를 요청하면 서버가 정의·배치·소유권을 검증하고 MSSQL에 커밋한다. UE UI/HTTP 연결 및 레이드 서버는 다음 단계다.

## 실행

필요 환경: Windows PowerShell, .NET SDK 10.0.203 이상(10.0 계열), 접근 가능한 SQL Server. 기본 SQL 인스턴스는 `.\SQLEXPRESS`, 신규 개발 DB는 `DeltaForce_Development`다. 기존 인벤토리 프로젝트 DB를 수정하거나 가져오지 않는다.

저장소 루트에서 실행한다.

```powershell
.\Server\Start-Server.ps1 -SeedDemoAccounts
```

사용자 요청에 따라 개발용 `demo`, `demo2` 비밀번호는 모두 `1234`다. 입력 창 없이 없는 계정을 생성하고, 기존 demo 계정의 비밀번호도 `1234`로 맞춘다. 기존 아이템·장착·Revision은 유지한다. 이후에는 다음 명령으로 시작한다.

```powershell
.\Server\Start-Server.ps1
```

기본 주소는 `http://127.0.0.1:5099`, 준비 확인은 `/health/ready`, OpenAPI는 `/openapi/v1.json`이다. 시작 스크립트는 설치된 .NET 10 또는 UE 5.8에 포함된 .NET 10 SDK를 선택한다. 다른 위치라면 `-DotNetPath 'C:\...\dotnet.exe'`를 지정한다. 다른 개발 DB는 `-ConnectionString` 또는 `ConnectionStrings__Inventory` 환경 변수로 지정한다. 비밀번호가 있는 연결 문자열은 명령 기록에 남기지 말고 환경 설정을 사용한다.

`Start-Server.ps1`은 로컬 개발 모드로 실행한다. 개발 모드는 EF Migration을 적용하며, demo seed는 명시적으로 요청했을 때만 수행한다. 생산 환경에서의 HTTPS·계정 발급·세션 공유·DB 배포 구성은 아직 제공하지 않는다.

## API

매칭 시작·취소와 기능 클라이언트 노드 연결은 [Matching.ko.md](Matching.ko.md)를 참조한다. 예약 API는 `/api/v1/raids/*`에 추가됐으며 API 계약 버전은 1.1.0이다.

| 메서드 | 경로 | 용도 |
| --- | --- | --- |
| POST | `/api/v1/auth/login` | `{userId,password}` → playerId, token, expiresAt |
| POST | `/api/v1/auth/logout` | 현재 토큰만 폐기; 저장 스냅샷 업로드 없음 |
| GET | `/api/v1/definitions` | 서버 정의와 definitionVersion |
| GET | `/api/v1/inventory` | 본인 Snapshot, 문자열 Revision |
| POST | `/api/v1/inventory/operations` | Move/Rotate/Equip/Unequip/Swap/Merge/Split |
| GET | `/api/v1/inventory/operations/{requestId}` | 원래 HTTP 상태를 포함한 저장 결과 조회 |

로그인 이후에는 `Authorization: Bearer <token>`을 보낸다. 토큰은 메모리에 해시로 보관하며 최대 8시간 유효하다. API 재시작 시 세션만 만료되고 데이터·요청 결과는 유지된다. 여러 API 프로세스의 메모리 세션은 공유되지 않는다. 현재 실행 구성은 단일 API 프로세스다.

이동 요청은 다음 형태다. GUID·Revision·definitionVersion·source는 먼저 조회한 Snapshot에서 가져온다.

```json
{
  "requestId": "11111111-1111-4111-8111-111111111111",
  "operation": "Move",
  "itemId": "22222222-2222-4222-8222-222222222222",
  "definitionVersion": "sha256:<조회한 정의 해시>",
  "expectedRevisions": [{"scopeId":"persistent:self","revision":"1"}],
  "source": {"type":"Grid","containerId":"33333333-3333-4333-8333-333333333333","gridIndex":0,"cellPosition":{"x":0,"y":0},"rotated":false},
  "destination": {"type":"EquipmentSlot","slotTag":"Slot.Helmet"}
}
```

`Equip`는 슬롯 목적지, `Unequip`는 슬롯 출발지와 Grid 목적지를 사용한다. `Rotate`는 같은 Grid 셀에서 회전만 변경한다. `Swap`은 `targetItemId`와 그 아이템의 현재 위치를 목적지로 보내며 두 위치 값을 교환한다. 양쪽 배치가 모두 가능해야 한다. `Merge`는 같은 정의·내구도 스택만 허용하며 quantity 생략 시 전량 합친다. `Split`은 원본 수량보다 작은 양수 quantity를 보내고 새 ItemId는 서버가 생성한다.

요청 타임아웃은 실패 확정이 아니다. 같은 RequestId·같은 payload로 결과 조회/재전송한다. 이미 저장된 결과는 Revision 검증보다 먼저 재생한다. 같은 키의 다른 payload는 `409 RequestPayloadMismatch`다. 새 의도에는 새 RequestId를 쓴다. M1에서 OperationRecord에는 TTL/삭제 정책을 적용하지 않는다.

성공 응답의 `updatedItems`는 전체 아이템 upsert다. `baseRevision`이 로컬 버전과 같을 때 Delta를 한 번만 적용하고, 다르면 Snapshot을 재조회한다. 클라이언트는 가격·크기·내구도·소유자·새 아이템을 임의 지정할 수 없다. 레거시 `PUT /api/inventory`는 이 서버에 존재하지 않는다.

## 데이터와 권한

- `Players`: 계정, PasswordHasher 해시, 영구 인벤토리 Revision.
- `ItemInstances`, `ItemLocations`, `Containers`: 전역 ItemId, 위치 1개, 장착 슬롯 유일성, 같은 플레이어에 속하는 FK.
- `OperationRecords`: 플레이어+RequestId 유일 키, 정규화 payload 해시, HTTP 상태와 결과 JSON.
- `RetiredItems`: 스택 합치기로 사라진 ID의 tombstone.

조회와 변경은 플레이어 행의 SQL `UPDLOCK,HOLDLOCK`을 같은 순서로 획득한다. M1은 플레이어 단위로 직렬화하여 Snapshot과 Revision을 일치시킨다. 아이템·위치·Revision·요청 결과는 하나의 DB 트랜잭션에서 커밋된다. 슬롯 교환은 트랜잭션 내부의 임시 Unplaced 상태를 거친다. 그 상태는 클라이언트 목적지로 허용하지 않는다. 레이드의 다중 플레이어/공유 컨테이너 권한 처리는 아직 없다.

원본 UE JSON에서 아이템 282개·레이아웃 46개를 독립 manifest로 가져왔다. 메인 창고는 `Stash_Main_00`(9×35)이고 demo 계정에는 헬멧·방탄복·가방·탄약 두 스택·동일 소총 두 개를 지급한다. 가방 내부 컨테이너는 장착할 때만 조작할 수 있으며, 해제해도 내용물을 보존한다.

**내구도 게임 규칙은 미구현**이다. 원본 DT에 최대 내구도 정의가 없으므로 M1 manifest와 seed는 0을 사용한다. 실제 게임 수치를 뜻하지 않는다. 안전금고·키홀더의 정의별 슬롯 매핑도 미확정이므로 해당 장착 요청은 `EquipmentSlotRuleMissing`으로 거절한다. 자동 회전·전체 재배치·자동 정렬은 실행하지 않는다.

정의 재생성(평상시 실행에 원본 저장소는 불필요):

```powershell
python .\Server\tools\export_definitions.py 'C:\Users\user1\Documents\Github\Grid-based-Inventory-System'
python .\Server\tools\build_contract.py
```

정의 파일 바이트의 SHA-256이 definitionVersion이다. 기존 아이템 정의의 크기·태그를 바꿀 때는 저장 데이터 검증/마이그레이션과 UE 버전 정합성을 먼저 설계한다.

## 검증

```powershell
.\Server\Test-Server.ps1
```

빌드, xUnit 도메인 검사, 실제 HTTP+MSSQL 통합 검사, 저장된 Postman 컬렉션 순서로 실행한다. 통합 검사는 `DeltaForce_Check_<랜덤 GUID>` DB를 새로 만들고 API 두 프로세스를 실행한다. 요청 재전송, 같은 Revision 경합, 조회/변경 경합, 슬롯 교환, SQL trigger로 주입한 커밋 실패 롤백, 프로세스 강제 종료 후 복원, 로그아웃을 검증한다. 테스트가 생성한 DB만 마지막에 제거한다. 개발 DB와 기존 프로젝트 DB는 건드리지 않는다. `-KeepDatabase`로 테스트 DB를 남길 수 있다.

다른 SQL 서버는 `DELTAFORCE_TEST_SQL` 환경 변수로 지정한다. 계정은 테스트 DB 생성·제거 권한이 필요하다. Postman CLI는 PATH에 있어야 하며 로그인 없이 로컬 컬렉션을 실행한다. `--no-report-events`로 외부 실행 결과 발행을 끈다. 임시 로그인 비밀번호는 임시 환경 파일에만 넣고 검사 후 삭제한다.

```powershell
# DB 없이 순수 규칙만 확인
.\Server\Test-Server.ps1 -SkipIntegration
```

검증 결과와 이어받기 항목은 [HANDOFF.ko.md](HANDOFF.ko.md)를 참조한다.

## DB 스키마 작업

`Server` 폴더에서 .NET 10을 사용한다. EF 도구 버전은 `dotnet-tools.json`에 고정했다.

```powershell
dotnet tool restore
dotnet ef migrations has-pending-model-changes --project .\src\DeltaForce.Infrastructure --startup-project .\src\DeltaForce.Api
```

Migration은 Infrastructure 프로젝트에 있으며 EnsureCreated를 사용하지 않는다. 기존 서버의 PlayerJson은 자동 이관하지 않는다. 원본 데이터 이관이 필요하면 GUID·태그·위치 검증과 별도 변환 도구를 추가해야 한다.
