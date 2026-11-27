# 매칭 시작·취소 — Reserved 단계

매칭 버튼의 서버 경로는 로그인 세션을 사용해 예약을 만들고 조회·취소하는 단계까지 구현했다. 기본 MapId는 `beacon`이며 `appsettings.json`의 `Raid:MapIds`에서 허용 목록을 관리한다.

## 클라이언트 함수

기능 클라이언트 위치: `C:\Users\user1\Documents\Github\Grid-based-Inventory-System`.

`Subsystem_Network`에서 다음 Blueprint Callable 함수를 제공한다.

| 함수/이벤트 | 용도 |
| --- | --- |
| `StartMatching(MapId)` | 매칭 예약. 비어 있으면 beacon. 불확실한 요청은 같은 RequestId로 재시도 |
| `CancelMatching()` | 접수된 예약 취소. 시작 응답이 오기 전에 눌러도 먼저 예약 결과를 확보한 뒤 취소 |
| `RefreshMatchingStatus()` | 서버 상태 재조회. 매칭 중에는 2초 주기로 자동 호출 |
| `IsMatchingRequestInFlight()` | 진행 중 HTTP 요청 확인; 재시도 버튼 제어 |
| `OnMatchingStateChanged(State, Reservation, Error)` | 서버 결과에 따른 상태 변경과 오류 전달 |
| `MatchingState` / `CurrentMatching` / `LastMatchingError` | UI가 재생성될 때 읽는 현재 상태 |

상태 흐름은 `Idle → Requesting → Matching → Canceling → Idle`이다. 마지막 Idle은 서버의 취소 확인 후 반영한다. timeout/5xx에서는 요청 키와 취소 의도를 보존한다. 에러 이후 시작 또는 취소를 다시 누르면 동일 키로 재시도한다.

로그인 완료 후 `/current-entry`를 조회하므로 서버 재시작·클라이언트 재로그인 시에도 기존 예약을 복구한다. 레거시 스냅샷 API에는 매칭 기능을 호출하지 않는다. 매칭 중 로그아웃은 `CancelMatchingBeforeLogout`으로 차단하므로 취소를 먼저 완료한다.

## WBP_Lobby 연결

현재 저장된 `WBP_Ready_1.OnClickedReady`는 FlipFlop으로 대기 애니메이션만 재생한다. 다음과 같이 서버 함수와 이벤트에 연결한다.

1. 로비 Init/BindFunc에서 Get Game Instance Subsystem → `Subsystem_Network` → Bind Event to `OnMatchingStateChanged`를 한 번 연결한다.
2. `OnClickedReady`에서 Subsystem의 `MatchingState`로 분기한다. Idle은 `StartMatching("beacon")`, Requesting/Matching은 `CancelMatching()`을 호출한다. Canceling에서는 요청 완료를 기다리며, 오류가 있고 HTTP가 진행 중이 아닐 때만 취소 재시도를 허용한다.
3. 이벤트의 State로 버튼/텍스트/`OnMatchingForWaiting` 애니메이션을 갱신한다. FlipFlop을 상태의 원본으로 사용하지 않는다. 같은 상태 이벤트는 애니메이션을 다시 시작하지 않게 처리한다.
4. 이벤트 Error가 비어 있지 않으면 오류를 표시한다. 네트워크 실패만으로 예약이 취소됐다고 표시하지 않는다.
5. Destruct에서 이 위젯이 바인딩한 동일 이벤트만 해제한다. 로비 재생성 시에는 `MatchingState`와 `CurrentMatching`을 먼저 읽어 표시한다.

UFUNCTION/USTRUCT가 추가됐으므로 에디터를 재시작한 뒤 새 노드를 검색한다. 이번 작업은 C++ 함수/API와 연결 지침을 추가했으며, 사용자가 편집 중인 WBP 바이너리는 수정하지 않았다.

## HTTP 계약

| 메서드 | 경로 | 입력/결과 |
| --- | --- | --- |
| POST | `/api/raids/reservations` | `{requestId,mapId}` → 201 Reserved |
| GET | `/api/raids/current-entry` | 본인 활성 예약 또는 404 EntryNotFound |
| GET | `/api/raids/{raidId}/entry` | 본인 해당 예약의 현재 상태 |
| POST | `/api/raids/{raidId}/entry/cancel` | `{requestId,entryId}` → 200 Cancelled |
| GET | `/api/raids/requests/{requestId}` | 저장된 결과와 원래 HTTP 상태 재생 |

모든 경로는 Bearer 세션 인증이 필요하다. 플레이어당 활성 예약은 하나다. 같은 키/같은 payload는 최초 결과를 재생하고 다른 payload는 409 RequestPayloadMismatch다. 다른 키로 활성 예약을 중복 생성하면 409 AlreadyMatching이다. 과거 취소 요청이 새 예약을 취소하지 않도록 RaidId+EntryId+계정으로 대상을 고정한다.

`RaidEntries`, `RaidRequestRecords`를 EF Migration으로 추가했다. 플레이어 행 잠금과 필터 유일 인덱스, 트랜잭션으로 생성·취소·멱등 기록의 일관성을 보장한다. Reserved는 장비 반입 commit 이전 단계이므로 아이템·장착·Revision을 변경하지 않는다.

현재 레이드 서버 배정, 인원 집계, 캐릭터 선택 확정, 반입 잠금, bootstrap/activate, 입장 티켓과 ClientTravel은 다음 단계다. 실제 서버 준비가 없으므로 시간 경과만으로 Matched를 만들지 않는다. 앱 강제 종료 시 예약은 유지되고, 다시 로그인하여 조회·취소할 수 있다. 예약 만료 정책은 아직 없다.

## 개발 서버 실행과 확인

```powershell
# 저장소 루트. PowerShell 정책으로 막히면 powershell.exe -ExecutionPolicy Bypass -File 형식 사용.
.\Server\Start-Server.ps1 -SeedDemoAccounts -Background
.\Server\Stop-Server.ps1
```

Background는 창을 띄우지 않고 실행하며 로그는 `Server/artifacts/server.stdout.log`, `server.stderr.log`에 남긴다. Stop은 PID·실행 파일·시작 시각이 일치하는 관리 대상 API만 종료한다. 기존 foreground 실행은 Ctrl+C로 종료한다.

검증 명령은 `.\Server\Test-Server.ps1`이다. 실제 MSSQL에서 중복/경쟁/타인 접근/과거 취소/예약·취소 rollback/서버 재시작 복구와 기존 인벤토리 회귀를 실행한다.

UE 에디터 콘솔의 자동화 테스트는 `Automation RunTests GridInventory.Network`다. 실행 인자에 `-MatchingTestBaseUrl=http://127.0.0.1:5099/api`을 추가하면 `MatchingLive`가 실제 demo/1234로 로그인 → 시작 직후 취소 → 재시작 → 조회 → 취소 → 로그아웃을 검증한다. 실행 전에 demo 계정에 사용 중인 매칭 예약이 없어야 한다.
