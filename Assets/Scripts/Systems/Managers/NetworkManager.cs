using System;
using System.Collections;
using System.Collections.Generic;
using Google.Protobuf;              // 패킷 포장/개봉 도구입니다. (Google.Protobuf.dll)
using GameProtocol;                 // protocol.proto 가 만들어 준 패킷 양식입니다. (csharp_namespace)
using NativeWebSocket;              // 실시간 통신(WebSocket) 라이브러리입니다.
using UnityEngine;                  // 유니티의 기본 도구입니다.
using UnityEngine.Networking;
using Vector3 = UnityEngine.Vector3;

// 네트워크 담당자 스크립트
public class NetworkManager : MonoBehaviour
{
    public static NetworkManager Instance { get; private set; }

    // ===== 서버 주소 설정 (유니티 Inspector 창에서 바꿀 수 있습니다) =====
    [Header("서버 주소")]
    // 로그인용 HTTP 주소입니다. 같은 PC 테스트는 localhost, 휴대폰 테스트는 PC의 내부 IP로 바꾸세요.
    [SerializeField] private string httpBaseUrl = "http://localhost:8080";
    // 실시간 WebSocket 기본 주소입니다. (토큰은 코드가 뒤에 자동으로 붙여 줍니다)
    [SerializeField] private string wsUrl = "ws://localhost:8080/ws";

    // ===== 내 캐릭터 설정 =====
    [Header("내 캐릭터 (비워 두면 무작위 좌표로 테스트합니다)")]
    // 좌표를 서버로 보낼 내 캐릭터의 위치 정보(Transform)입니다. 씬의 캐릭터를 끌어다 놓으세요.
    [SerializeField] private Transform myCharacter;
    // 방향키/WASD로 캐릭터를 움직일 때의 속도입니다. (1초에 몇 칸 움직일지)
    [SerializeField] private float moveSpeed = 5f;
    // 캐릭터가 움직이는 동안 서버로 좌표를 보내는 간격(초)입니다. 0.1 이면 1초에 10번 보냅니다.
    [SerializeField] private float sendInterval = 0.1f;

    // ===== 상대 캐릭터 설정 =====
    [Header("상대 캐릭터")]
    // 다른 플레이어가 들어오면 복제해서 만들 프리팹(캐릭터 원본)입니다. 비워 두면 빨간 큐브가 대신 만들어집니다.
    [SerializeField] private GameObject otherPlayerPrefab;
    // 상대 캐릭터가 목표 위치로 부드럽게 따라가는 속도입니다. 클수록 빨리 따라갑니다.
    [SerializeField] private float otherPlayerLerpSpeed = 15f;

    // ===== 자동 재접속 설정 =====
    [Header("자동 재접속(Auto-Reconnect) 설정")]
    // 재접속을 최대 몇 번까지 시도할지 정합니다.
    [SerializeField] private int maxReconnectAttempts = 8;
    // 첫 재접속 시도 전에 기다리는 시간(초)입니다. 실패할 때마다 2배로 늘어납니다.
    [SerializeField] private float baseReconnectDelay = 1f;
    // 대기 시간이 아무리 늘어나도 넘지 않는 상한(초)입니다.
    [SerializeField] private float maxReconnectDelay = 15f;
    // 연결을 한 번 시도했을 때 성공을 기다려 주는 최대 시간(초)입니다.
    [SerializeField] private float connectTimeout = 5f;
    // 몇 번 시도할 때마다 HTTP로 다시 로그인(새 토큰 발급)할지 정합니다.
    [SerializeField] private int reLoginEvery = 3;

    // ===== 내 정보 =====
    public long MyPlayerId { get; private set; } = 0;     // 서버가 나에게 붙여 준 플레이어 번호입니다. 로그인 전에는 0
    public bool IsLoggedIn { get; private set; } = false;  // 로그인이 성공했는지 기억
    // 지금 웹소켓이 정상적으로 열려 있는지 알려 주는 표시입니다. (읽기 전용)
    public bool IsConnected
    {
        // 웹소켓이 만들어져 있고 상태가 "열림"이면 true 를 돌려줍니다.
        get { return websocket != null && websocket.State == WebSocketState.Open; }
    }

    #region ===== 외부에 알려 주는 신호(이벤트) =====
    public event Action<long, Vector3> OnOtherPlayerMoved; // 다른 플레이어의 위치 정보를 받았을 때 알려 주는 신호입니다. (플레이어 번호, 위치)
    public event Action<long> OnOtherPlayerEntered;     // 다른 플레이어의 캐릭터가 새로 만들어졌을 때 알려 주는 신호입니다. (플레이어 번호)
    public event Action<long> OnOtherPlayerLeft;     // 다른 플레이어가 완전히 나가서 캐릭터가 삭제되었을 때 알려 주는 신호입니다. (플레이어 번호)
    public event Action OnReconnecting;     // 연결이 끊겨서 재접속을 시작할 때 알려 주는 신호입니다. ("재접속 중..." 표시에 쓰세요)
    public event Action OnConnected;  // 연결(또는 재접속)에 성공했을 때 알려 주는 신호입니다. ("재접속 중..." 표시를 끄는 데 쓰세요)
    public event Action OnConnectionFailed;     // 재접속을 모두 실패해서 포기했을 때 알려 주는 신호입니다. ("연결 실패 팝업"에 쓰세요)
    #endregion

    // ===== 내부에서 쓰는 변수 =====
    // 이 기기를 서버에 알려 주는 ID입니다. 재로그인 때도 같은 ID를 써야 같은 플레이어로 인식되어 한 번 만든 뒤 보관합니다.
    private string deviceId = "";
    // 서버가 발급해 준 입장 팔찌(세션 토큰)를 메모리에 기억해 두는 변수입니다.
    private string sessionToken = "";
    // 서버와 연결된 웹소켓 통로입니다.
    private WebSocket websocket;
    // 지금 재접속 작업이 진행 중인지 나타내는 표시입니다. (중복 시작 방지)
    private bool isReconnecting = false;
    // "재접속이 필요하다"는 요청 표시입니다. 다른 스레드에서도 켜질 수 있어 volatile 로 안전하게 둡니다.
    private volatile bool reconnectRequested = false;
    // 이번에 시도한 웹소켓이 닫혔는지 나타내는 표시입니다. (재접속 성공/실패 판단용)
    private volatile bool socketClosedFlag = false;
    // "연결이 막 열렸으니 내 현재 위치를 서버에 알려야 한다"는 표시입니다. (처음 접속/재접속 직후 한 번 보냅니다)
    private volatile bool needSendInitialPosition = false;
    // 게임을 끝내는 중인지 나타내는 표시입니다. 이때는 재접속을 하면 안 됩니다.
    private bool isQuitting = false;
    // 마지막으로 이동 패킷을 보낸 시각입니다. (너무 자주 보내지 않기 위한 용도)
    private float lastSendTime = 0f;

    // 화면에 만들어 둔 상대 캐릭터 목록입니다. (플레이어 번호 -> 캐릭터 오브젝트)
    private readonly Dictionary<long, GameObject> otherPlayers = new Dictionary<long, GameObject>();
    // 상대 캐릭터가 향해 가야 할 목표 위치 목록입니다. (플레이어 번호 -> 서버가 알려 준 최신 위치)
    private readonly Dictionary<long, Vector3> targetPositions = new Dictionary<long, Vector3>();

    // ===== Awake: 오브젝트가 만들어지는 가장 첫 순간에 한 번 실행됩니다 =====
    private void Awake()
    {
        // 이미 다른 네트워크 담당자가 있다면
        if (Instance != null && Instance != this)
        {
            // 중복이니 이 오브젝트를 없앱니다.
            Destroy(gameObject);
            // 여기서 함수를 끝냅니다.
            return;
        }

        // 내가 유일한 담당자로 등록됩니다.
        Instance = this;
        // 씬(장면)이 바뀌어도 이 오브젝트가 사라지지 않게 합니다.
        DontDestroyOnLoad(gameObject);
    }

    // ===== Start: 게임이 시작될 때 한 번 실행됩니다 =====
    private void Start()
    {
        // 게임이 시작되면 자동으로 "로그인 -> 웹소켓 연결" 과정을 시작합니다.
        StartCoroutine(InitialConnectRoutine());
    }

    // ===== Update: 매 프레임(화면이 갱신될 때마다) 실행됩니다 =====
    private void Update()
    {
        // 웹소켓은 아래 줄을 매 프레임 호출해야 받은 메시지를 처리합니다.
        // (WebGL이 아닌 환경에서만 필요하므로 조건부로 넣었습니다.)
#if !UNITY_WEBGL || UNITY_EDITOR
        // 웹소켓이 만들어져 있다면
        if (websocket != null)
        {
            // 서버에서 받은 메시지를 처리하도록 일을 시킵니다.
            websocket.DispatchMessageQueue();
        }
#endif

        // 상대 캐릭터들을 목표 위치로 부드럽게 움직입니다.
        SmoothMoveOtherPlayers();

        // 연결이 끊겨서 재접속 요청이 들어왔고, 아직 재접속 중이 아니며, 게임을 끝내는 중도 아니라면
        if (reconnectRequested && !isReconnecting && !isQuitting)
        {
            // 재접속 요청 표시를 끕니다. (한 번만 처리하기 위해)
            reconnectRequested = false;
            // 백그라운드에서 재접속을 시작합니다.
            StartCoroutine(ReconnectRoutine());
        }

        // 웹소켓이 열려 있지 않으면 아래 입력 처리는 하지 않습니다.
        if (!IsConnected)
        {
            // 여기서 이번 프레임의 Update 를 끝냅니다.
            return;
        }

        // 연결이 막 열렸고 내 캐릭터가 있다면
        if (needSendInitialPosition && myCharacter != null)
        {
            // 표시를 끕니다. (한 번만 보내기 위해)
            needSendInitialPosition = false;
            // 내 현재 위치를 서버 칠판에 적어 달라고 보냅니다. (다른 사람들이 내 위치를 바로 알 수 있습니다)
            SendMove(myCharacter.position);
        }

        // 이번 프레임에 캐릭터가 움직였는지 기억하는 표시입니다.
        bool moved = false;

        // 내 캐릭터가 연결되어 있다면 방향키/WASD 로 움직입니다.
        if (myCharacter != null)
        {
            // 좌우 입력값(-1 ~ 1)을 읽습니다. (A/D, 좌/우 방향키)
            float h = Input.GetAxisRaw("Horizontal");
            // 앞뒤 입력값(-1 ~ 1)을 읽습니다. (W/S, 위/아래 방향키)
            float v = Input.GetAxisRaw("Vertical");

            // 입력이 하나라도 있다면
            if (h != 0f || v != 0f)
            {
                // 대각선으로 가도 속도가 빨라지지 않게 방향을 정리(normalized)해서 캐릭터를 움직입니다.
                myCharacter.position += new Vector3(h, 0f, v).normalized * moveSpeed * Time.deltaTime;
                // 움직였다고 표시합니다.
                moved = true;
            }
        }

        // 움직였고, 마지막 전송 후 정해 둔 간격(sendInterval)이 지났다면
        if (moved && Time.time - lastSendTime >= sendInterval)
        {
            // 현재 캐릭터 좌표를 서버로 보냅니다.
            SendMove(myCharacter.position);
        }

        // 마우스 왼쪽 버튼을 눌렀다면
        if (Input.GetMouseButtonDown(0))
        {
            // 보낼 좌표를 담을 변수입니다.
            Vector3 pos;

            // 내 캐릭터가 연결되어 있다면
            if (myCharacter != null)
            {
                // 캐릭터의 현재 좌표를 보냅니다.
                pos = myCharacter.position;
            }
            // 연결되어 있지 않다면
            else
            {
                // 테스트용 임의 좌표(-5 ~ 5 사이 무작위 값)를 만듭니다.
                pos = new Vector3(
                    UnityEngine.Random.Range(-5f, 5f),   // 좌우 위치를 무작위로 정합니다.
                    0f,                                  // 높이는 0으로 둡니다.
                    UnityEngine.Random.Range(-5f, 5f));  // 앞뒤 위치를 무작위로 정합니다.
            }

            // 만든 좌표를 서버로 보냅니다.
            SendMove(pos);
            // 보낸 내용을 Console 창에 남깁니다. (동작 확인용)
            Debug.Log("[이동 전송] 플레이어 " + MyPlayerId + " -> " + pos);
        }

        // 스페이스바를 눌렀다면
        if (Input.GetKeyDown(KeyCode.Space))
        {
            // 원점(0, 0, 0) 좌표를 서버로 보냅니다. (키보드 테스트용)
            SendMove(Vector3.zero);
            // 보낸 내용을 Console 창에 남깁니다.
            Debug.Log("[이동 전송] 플레이어 " + MyPlayerId + " -> 원점(0,0,0)");
        }
    }

    // ===== 상대 캐릭터를 목표 위치로 부드럽게 움직이는 함수 =====
    private void SmoothMoveOtherPlayers()
    {
        // 이번 프레임에 쓸 보간 비율을 계산합니다. (프레임 속도가 달라도 같은 느낌이 나도록 지수 방식을 씁니다)
        float t = 1f - Mathf.Exp(-otherPlayerLerpSpeed * Time.deltaTime);

        // 화면에 있는 모든 상대 캐릭터를 하나씩 살펴봅니다.
        foreach (KeyValuePair<long, GameObject> pair in otherPlayers)
        {
            // 캐릭터가 이미 파괴되었다면 (씬이 바뀐 경우 등)
            if (pair.Value == null)
            {
                // 건너뜁니다.
                continue;
            }

            // 이 캐릭터의 목표 위치를 찾습니다.
            Vector3 target;
            // 목표 위치가 있다면
            if (targetPositions.TryGetValue(pair.Key, out target))
            {
                // 현재 위치에서 목표 위치 쪽으로 조금 이동합니다.
                pair.Value.transform.position = Vector3.Lerp(pair.Value.transform.position, target, t);
            }
        }
    }

    // ===== 처음 시작할 때의 연결 과정: 로그인 -> 웹소켓 연결 =====
    private IEnumerator InitialConnectRoutine()
    {
        // 로그인 성공 여부를 담을 변수입니다.
        bool loginOk = false;

        // 로그인 과정을 실행하고, 끝나면 결과를 loginOk 에 담습니다.
        yield return LoginOnce(result => loginOk = result);

        // 로그인에 성공했다면
        if (loginOk)
        {
            // 곧바로 실시간 연결(WebSocket)을 시작합니다.
            ConnectWebSocket();
        }
        // 로그인에 실패했다면 (서버가 꺼져 있는 경우 등)
        else
        {
            // 게임을 포기하지 않고 재접속 루틴으로 서버가 켜질 때까지 다시 시도합니다.
            Debug.LogWarning("[로그인 실패] 자동 재시도를 시작합니다.");
            // 재접속 루틴을 시작합니다.
            StartCoroutine(ReconnectRoutine());
        }
    }

    // ===== 로그인 1회 시도 (HTTP) =====
    // onDone: 로그인이 끝났을 때 "성공했나요(true/false)"를 알려 줄 연락 수단입니다.
    private IEnumerator LoginOnce(Action<bool> onDone)
    {
        // 기기 ID가 아직 정해지지 않았다면 (처음 한 번만 만듭니다)
        if (string.IsNullOrEmpty(deviceId))
        {
            // 이 기기의 고유 ID를 가져옵니다.
            deviceId = SystemInfo.deviceUniqueIdentifier;

#if UNITY_EDITOR
            // [멀티 테스트용] 유니티 에디터에서 실행할 때만, 기기 ID 뒤에 무작위 4자리 숫자를 붙입니다.
            // 그러면 같은 컴퓨터에서 여러 개를 켜도 서버가 서로 다른 기기로 인식합니다.
            deviceId = deviceId + "_" + UnityEngine.Random.Range(1000, 10000);
            // 에디터에서 사용하는 가짜 기기 ID를 Console 창에 남깁니다.
            Debug.Log("[에디터 테스트] 가상 기기 ID = " + deviceId);
#endif
        }

        // 로그인 요청 양식을 만들고 기기 ID를 채웁니다.
        LoginRequest loginReq = new LoginRequest
        {
            DeviceId = deviceId // proto의 device_id 가 C#에서는 DeviceId 가 됩니다.
        };

        // 양식을 전송 가능한 바이너리 데이터로 "포장"(직렬화)합니다.
        byte[] bodyBytes = loginReq.ToByteArray();

        // 서버의 /login 주소로 보낼 HTTP 요청을 만듭니다. (POST 방식)
        using (UnityWebRequest request = new UnityWebRequest(httpBaseUrl + "/login", UnityWebRequest.kHttpVerbPOST))
        {
            // 보낼 데이터(포장한 바이너리)를 요청에 실어 줍니다.
            request.uploadHandler = new UploadHandlerRaw(bodyBytes);
            // 서버가 돌려줄 응답을 받아 둘 그릇을 준비합니다.
            request.downloadHandler = new DownloadHandlerBuffer();
            // "이 데이터는 프로토콜 버퍼입니다"라고 표시합니다. (서버와 같은 표기)
            request.SetRequestHeader("Content-Type", "application/x-protobuf");
            // 서버가 5초 안에 답하지 않으면 실패로 처리합니다. (무한 대기 방지)
            request.timeout = 5;

            // 요청을 보내고 응답이 올 때까지 기다립니다.
            yield return request.SendWebRequest();

            // 통신 자체가 실패했다면 (서버가 꺼져 있거나 주소가 틀리거나 인터넷이 끊긴 경우 등)
            if (request.result != UnityWebRequest.Result.Success)
            {
                // 오류 내용을 유니티 Console 창에 남깁니다.
                Debug.LogWarning("[로그인 실패] 서버 통신 오류: " + request.error);
                // 호출한 쪽에 "실패했어요"라고 알립니다.
                onDone(false);
                // 여기서 코루틴을 끝냅니다.
                yield break;
            }

            // 서버가 돌려준 바이너리 데이터를 꺼냅니다.
            byte[] responseBytes = request.downloadHandler.data;

            // 응답 데이터를 담을 변수를 준비합니다.
            LoginResponse loginRes;
            // 바이너리 데이터를 로그인 응답 양식으로 "개봉"(역직렬화)합니다.
            try
            {
                // 개봉을 시도합니다.
                loginRes = LoginResponse.Parser.ParseFrom(responseBytes);
            }
            // 양식에 맞지 않는 데이터라서 개봉이 실패했다면
            catch (Exception e)
            {
                // 오류 내용을 Console 창에 남깁니다.
                Debug.LogError("[로그인 실패] 응답 해석 오류: " + e.Message);
                // 호출한 쪽에 "실패했어요"라고 알립니다.
                onDone(false);
                // 여기서 코루틴을 끝냅니다.
                yield break;
            }

            // 서버가 "실패"라고 답했거나 토큰을 비워서 보냈다면
            if (!loginRes.Success || string.IsNullOrEmpty(loginRes.SessionToken))
            {
                // 서버가 알려 준 실패 이유를 Console 창에 남깁니다.
                Debug.LogError("[로그인 거절] " + loginRes.Message);
                // 호출한 쪽에 "실패했어요"라고 알립니다.
                onDone(false);
                // 여기서 코루틴을 끝냅니다.
                yield break;
            }

            // 서버가 준 플레이어 번호를 내 번호로 저장합니다.
            MyPlayerId = loginRes.PlayerId;
            // 서버가 준 입장 팔찌(세션 토큰)를 메모리에 기억해 둡니다.
            sessionToken = loginRes.SessionToken;
            // 로그인 완료 표시를 켭니다.
            IsLoggedIn = true;
            // 성공 기록을 Console 창에 남깁니다. (토큰 전체는 보안상 출력하지 않습니다)
            Debug.Log("[로그인 성공] 내 플레이어 번호 = " + MyPlayerId + " / 토큰 수령 완료");
        }

        // 호출한 쪽에 "성공했어요"라고 알립니다.
        onDone(true);
    }

    // ===== 실시간 연결 1회 시도 (WebSocket) =====
    // async: 연결을 기다리는 동안 게임이 멈추지 않게 해 주는 방식의 함수입니다.
    private async void ConnectWebSocket()
    {
        // 이전 연결이 남아 있다면 따로 챙겨 둡니다.
        WebSocket oldSocket = websocket;
        // 이전 연결 참조를 비웁니다. (이전 연결의 OnClose 가 재접속을 일으키지 않게 하기 위함)
        websocket = null;
        // 이전 연결이 있었다면
        if (oldSocket != null)
        {
            // 조용히 닫아서 정리합니다.
            CloseQuietly(oldSocket);
        }

        // 이번 시도의 "닫힘" 표시를 초기화합니다.
        socketClosedFlag = false;

        // 기본 주소 뒤에 "?token=내토큰"을 붙여서 서버에 입장 팔찌를 제출합니다.
        // EscapeURL은 토큰에 특수문자가 있어도 주소가 깨지지 않게 안전하게 바꿔 줍니다.
        string urlWithToken = wsUrl + "?token=" + UnityWebRequest.EscapeURL(sessionToken);

        // 토큰이 붙은 주소로 연결할 새 웹소켓 통로를 만듭니다. (한 번 닫힌 통로는 재사용할 수 없어서 매번 새로 만듭니다)
        WebSocket socket = new WebSocket(urlWithToken);
        // 새 통로를 현재 사용 중인 통로로 등록합니다.
        websocket = socket;

        // 연결이 성공적으로 열렸을 때 실행할 내용입니다.
        socket.OnOpen += () =>
        {
            // 연결 성공을 Console 창에 남깁니다.
            Debug.Log("[WebSocket] 서버 인증 통과 및 실시간 연결 성공!");
            // "내 현재 위치를 서버에 알려야 한다"고 표시합니다. (실제 전송은 Update 에서 합니다)
            needSendInitialPosition = true;
            // 연결 성공을 다른 스크립트에 알립니다.
            OnConnected?.Invoke();
        };

        // 오류가 났을 때 실행할 내용입니다. (토큰이 틀려 서버가 차단한 경우도 여기로 옵니다)
        socket.OnError += (errorMsg) =>
        {
            // 오류 내용을 Console 창에 남깁니다.
            Debug.LogWarning("[WebSocket] 오류: " + errorMsg);
        };

        // 연결이 닫혔을 때 실행할 내용입니다. (지하철 터널 등으로 끊긴 경우도 여기로 옵니다)
        socket.OnClose += (closeCode) =>
        {
            // 이미 새 통로로 교체된 "옛 통로"의 종료 알림이라면 무시합니다.
            if (socket != websocket)
            {
                // 여기서 끝냅니다.
                return;
            }

            // 이번 통로가 닫혔다고 표시합니다. (재접속 루틴이 실패를 알아채는 용도)
            socketClosedFlag = true;
            // 연결 종료를 Console 창에 남깁니다.
            Debug.Log("[WebSocket] 연결 종료 (코드: " + closeCode + ")");

            // 재접속 중이 아니고 게임을 끝내는 중도 아니라면
            if (!isReconnecting && !isQuitting)
            {
                // "재접속이 필요하다"고 표시만 켭니다. (실제 시작은 메인 스레드인 Update 에서 합니다)
                reconnectRequested = true;
            }
        };

        // 서버에서 메시지가 도착했을 때 실행할 내용입니다.
        socket.OnMessage += (bytes) =>
        {
            // 도착한 데이터를 처리하는 함수로 넘깁니다.
            HandleServerMessage(bytes);
        };

        // 연결을 시도합니다. 연결이 유지되는 동안은 여기서 계속 기다리고, 연결이 끝나면 아래로 내려갑니다.
        try
        {
            // 서버에 연결을 시도합니다.
            await socket.Connect();
        }
        // 연결 시도 중 예외가 터졌다면
        catch (Exception e)
        {
            // 오류를 Console 창에 남깁니다.
            Debug.LogWarning("[WebSocket] 연결 시도 실패: " + e.Message);
            // 이번 통로가 현재 통로라면
            if (socket == websocket)
            {
                // 실패했다고 표시합니다.
                socketClosedFlag = true;
                // 재접속 중이 아니고 게임을 끝내는 중도 아니라면
                if (!isReconnecting && !isQuitting)
                {
                    // 재접속이 필요하다고 표시합니다.
                    reconnectRequested = true;
                }
            }
        }
    }

    // ===== 옛 웹소켓을 조용히 닫는 도우미 =====
    private async void CloseQuietly(WebSocket socket)
    {
        // 닫는 도중 오류가 나도 게임에 영향이 없도록 감쌉니다.
        try
        {
            // 연결을 닫습니다.
            await socket.Close();
        }
        // 이미 닫혀 있는 등의 오류는
        catch (Exception)
        {
            // 무시합니다.
        }
    }

    // ===== 자동 재접속 루틴 (끊김 대응의 핵심) =====
    // 연결이 끊겼을 때 유저를 게임에서 내보내지 않고, 백그라운드에서 다시 붙을 때까지 시도합니다.
    // 서버는 15초 동안 내 자리를 비워 두므로, 그 안에 붙으면 같은 방 같은 자리로 복귀합니다.
    private IEnumerator ReconnectRoutine()
    {
        // "재접속 진행 중" 표시를 켭니다. (중복 시작 방지)
        isReconnecting = true;
        // 재접속 시작을 다른 스크립트에 알립니다. ("재접속 중..." 화면 표시용)
        OnReconnecting?.Invoke();
        // 재접속 시작을 Console 창에 남깁니다.
        Debug.Log("[재접속] 연결이 끊겨 자동 재접속을 시작합니다.");

        // 최대 시도 횟수만큼 반복합니다.
        for (int attempt = 1; attempt <= maxReconnectAttempts; attempt++)
        {
            // 대기 시간을 계산합니다. 1배, 2배, 4배, 8배... 로 늘어나되 상한을 넘지 않습니다.
            float delay = Mathf.Min(baseReconnectDelay * Mathf.Pow(2f, attempt - 1), maxReconnectDelay);
            // 몇 번째 시도인지 Console 창에 남깁니다.
            Debug.Log("[재접속] " + attempt + "/" + maxReconnectAttempts + "번째 시도 (" + delay + "초 후)");

            // 정해진 시간만큼 기다립니다. (게임 일시정지 상태에서도 흐르는 현실 시간 기준)
            yield return new WaitForSecondsRealtime(delay);

            // 기다리는 사이 게임이 종료되었다면
            if (isQuitting)
            {
                // 재접속 표시를 끄고
                isReconnecting = false;
                // 루틴을 끝냅니다.
                yield break;
            }

            // 아직 로그인한 적이 없거나, 정해진 횟수마다 한 번씩은 새 토큰을 받기 위해 다시 로그인합니다.
            bool needLogin = !IsLoggedIn || (attempt > 1 && attempt % reLoginEvery == 0);
            // 다시 로그인이 필요하다면
            if (needLogin)
            {
                // 로그인 성공 여부를 담을 변수입니다.
                bool loginOk = false;
                // 로그인을 시도하고 결과를 기다립니다.
                yield return LoginOnce(result => loginOk = result);
                // 로그인에 실패했다면 (아직 서버/인터넷이 복구되지 않은 상태)
                if (!loginOk)
                {
                    // 이번 시도는 건너뛰고 다음 시도로 넘어갑니다.
                    continue;
                }
            }

            // 저장해 둔 토큰으로 웹소켓 연결을 시도합니다.
            ConnectWebSocket();

            // 이번 시도에서 기다린 시간을 기록할 변수입니다.
            float waited = 0f;
            // 연결이 열리거나, 닫혀 버리거나, 제한 시간이 지날 때까지 매 프레임 확인하며 기다립니다.
            while (waited < connectTimeout && !IsConnected && !socketClosedFlag)
            {
                // 지난 프레임만큼 기다린 시간을 더합니다.
                waited += Time.unscaledDeltaTime;
                // 다음 프레임까지 쉽니다.
                yield return null;
            }

            // 연결에 성공했다면
            if (IsConnected)
            {
                // 성공 기록을 Console 창에 남깁니다.
                Debug.Log("[재접속] 성공! 다시 실시간 연결되었습니다.");
                // 재접속 진행 표시를 끕니다.
                isReconnecting = false;
                // 루틴을 끝냅니다.
                yield break;
            }

            // 연결이 아직 열리지 못했다면 다음 시도로 넘어갑니다.
            Debug.LogWarning("[재접속] " + attempt + "번째 시도 실패");
        }

        // 모든 시도에 실패했다면 포기합니다.
        isReconnecting = false;
        // 포기 사실을 Console 창에 남깁니다.
        Debug.LogError("[재접속] 모든 시도에 실패했습니다. 네트워크 상태를 확인해 주세요.");
        // 포기 사실을 다른 스크립트에 알립니다. ("연결 실패 팝업"을 띄우는 데 쓰세요)
        OnConnectionFailed?.Invoke();
    }

    // ===== 포기한 뒤 유저가 "다시 시도" 버튼을 눌렀을 때 호출하는 함수 =====
    public void RetryNow()
    {
        // 이미 재접속 중이거나 연결되어 있다면 할 일이 없습니다.
        if (isReconnecting || IsConnected)
        {
            // 여기서 끝냅니다.
            return;
        }

        // 재접속 루틴을 새로 시작합니다.
        StartCoroutine(ReconnectRoutine());
    }

    // ===== 모바일 앱이 백그라운드로 갔다가 돌아왔을 때 실행됩니다 =====
    // paused 가 true 면 앱을 내린 것이고, false 면 다시 앱으로 돌아온 것입니다.
    private void OnApplicationPause(bool paused)
    {
        // 앱으로 돌아왔고, 로그인한 적이 있으며, 연결이 끊겨 있고, 재접속 중도 아니라면
        if (!paused && IsLoggedIn && !IsConnected && !isReconnecting && !isQuitting)
        {
            // 재접속이 필요하다고 표시합니다. (실제 시작은 Update 에서 합니다)
            reconnectRequested = true;
        }
    }

    // ===== 서버에서 받은 패킷 처리 (ServerMessage 포장지를 열어서 종류별로 나눕니다) =====
    private void HandleServerMessage(byte[] bytes)
    {
        // 개봉한 결과를 담을 변수를 준비합니다.
        ServerMessage msg;
        // 바이너리 데이터를 서버 패킷 포장지로 "개봉"합니다.
        try
        {
            // 개봉을 시도합니다.
            msg = ServerMessage.Parser.ParseFrom(bytes);
        }
        // 양식에 맞지 않는 데이터라서 실패했다면
        catch (Exception e)
        {
            // 오류를 Console 창에 남기고
            Debug.LogWarning("[WebSocket] 서버 패킷 해석 실패: " + e.Message);
            // 이 메시지는 무시합니다.
            return;
        }

        // 포장지 안에 어떤 종류가 들어 있는지에 따라 처리를 나눕니다.
        switch (msg.PayloadCase)
        {
            // 방 전체 위치 목록(스냅샷)이 들어 있다면
            case ServerMessage.PayloadOneofCase.Snapshot:
                // 스냅샷 처리 함수로 넘깁니다.
                HandleSnapshot(msg.Snapshot);
                // 이 종류의 처리를 마칩니다.
                break;

            // 새 플레이어 입장 알림이 들어 있다면
            case ServerMessage.PayloadOneofCase.Enter:
                // 입장 알림 처리 함수로 넘깁니다.
                HandleEnter(msg.Enter);
                // 이 종류의 처리를 마칩니다.
                break;

            // 플레이어 퇴장 알림이 들어 있다면
            case ServerMessage.PayloadOneofCase.Leave:
                // 퇴장한 플레이어의 캐릭터를 삭제합니다.
                RemoveOtherPlayer(msg.Leave.PlayerId);
                // 이 종류의 처리를 마칩니다.
                break;

            // 아무것도 들어 있지 않거나 모르는 종류라면
            default:
                // 무시합니다.
                break;
        }
    }

    // ===== 방 스냅샷 처리: 방 안 모든 사람의 최신 위치로 화면을 맞춥니다 =====
    private void HandleSnapshot(RoomSnapshot snapshot)
    {
        // 이번 스냅샷에 들어 있는 상대 플레이어 번호들을 적어 둘 집합입니다.
        HashSet<long> idsInSnapshot = new HashSet<long>();

        // 스냅샷에 담긴 플레이어를 한 명씩 살펴봅니다.
        foreach (PlayerState p in snapshot.Players)
        {
            // 나 자신이라면 (내 캐릭터는 내가 직접 움직이므로)
            if (p.PlayerId == MyPlayerId)
            {
                // 건너뜁니다.
                continue;
            }

            // 위치 정보가 비어 있다면
            if (p.Position == null)
            {
                // 건너뜁니다.
                continue;
            }

            // 이 상대 플레이어를 "명단에 있음"으로 적어 둡니다.
            idsInSnapshot.Add(p.PlayerId);
            // 패킷 좌표를 유니티가 쓰는 Vector3 로 바꿉니다.
            Vector3 pos = new Vector3(p.Position.X, p.Position.Y, p.Position.Z);

            // 이 상대의 캐릭터가 화면에 이미 있는지 찾아봅니다.
            GameObject existing;
            bool found = otherPlayers.TryGetValue(p.PlayerId, out existing) && existing != null;

            // 화면에 없다면
            if (!found)
            {
                // 새로 만듭니다. (만들 때 바로 그 위치에 놓습니다)
                SpawnOtherPlayer(p.PlayerId, pos);
            }
            // 이미 있다면
            else
            {
                // 목표 위치만 갱신합니다. (실제 이동은 Update 에서 부드럽게 따라갑니다)
                targetPositions[p.PlayerId] = pos;
            }

            // 이 신호를 기다리는 다른 스크립트에게 "상대가 움직였어요"라고 알려 줍니다.
            OnOtherPlayerMoved?.Invoke(p.PlayerId, pos);
        }

        // 스냅샷 명단에 없는데 화면에는 남아 있는 캐릭터를 찾아 삭제할 목록입니다.
        // (Leave 패킷을 놓쳤거나 재접속 중에 나간 사람을 정리하기 위함입니다)
        List<long> toRemove = new List<long>();
        // 화면에 있는 모든 상대 캐릭터를 살펴봅니다.
        foreach (long id in otherPlayers.Keys)
        {
            // 이번 스냅샷 명단에 없다면
            if (!idsInSnapshot.Contains(id))
            {
                // 삭제 목록에 넣습니다.
                toRemove.Add(id);
            }
        }

        // 삭제 목록의 캐릭터들을 하나씩 지웁니다. (반복 중에 사전을 직접 고치면 오류가 나서 따로 모아 둔 뒤 지웁니다)
        foreach (long id in toRemove)
        {
            // 캐릭터를 삭제합니다.
            RemoveOtherPlayer(id);
        }
    }

    // ===== 입장 알림 처리: 새로 들어온 사람의 캐릭터를 만듭니다 =====
    private void HandleEnter(PlayerEnter enter)
    {
        // 나 자신의 입장 알림이라면
        if (enter.PlayerId == MyPlayerId)
        {
            // 무시합니다.
            return;
        }

        // 이미 화면에 있는 캐릭터인지 확인합니다.
        GameObject existing;
        // 이미 있다면
        if (otherPlayers.TryGetValue(enter.PlayerId, out existing) && existing != null)
        {
            // 중복 생성하지 않습니다.
            return;
        }

        // 입장 위치를 정합니다. (위치 정보가 없으면 원점)
        Vector3 pos = enter.Position == null
            ? Vector3.zero
            : new Vector3(enter.Position.X, enter.Position.Y, enter.Position.Z);

        // 캐릭터를 새로 만듭니다.
        SpawnOtherPlayer(enter.PlayerId, pos);
    }

    // ===== 상대 캐릭터 생성 =====
    private GameObject SpawnOtherPlayer(long playerId, Vector3 pos)
    {
        // 만들어질 캐릭터를 담을 변수입니다.
        GameObject obj;

        // 프리팹(캐릭터 원본)이 Inspector 에 연결되어 있다면
        if (otherPlayerPrefab != null)
        {
            // 원본을 복제해서 해당 위치에 만듭니다. (Instantiate = 복제 생성)
            obj = Instantiate(otherPlayerPrefab, pos, Quaternion.identity);
        }
        // 프리팹이 없다면
        else
        {
            // 테스트용으로 기본 큐브를 만듭니다.
            obj = GameObject.CreatePrimitive(PrimitiveType.Cube);
            // 해당 위치에 놓습니다.
            obj.transform.position = pos;
            // 큐브에 붙어 있는 그리기 도구를 가져옵니다.
            Renderer rend = obj.GetComponent<Renderer>();
            // 그리기 도구가 있다면
            if (rend != null)
            {
                // 상대방이라는 것을 알 수 있게 빨간색으로 칠합니다.
                rend.material.color = Color.red;
            }
        }

        // 하이어라키 창에서 알아보기 쉽게 이름을 붙입니다.
        obj.name = "OtherPlayer_" + playerId;
        // 상대 캐릭터 목록에 등록합니다.
        otherPlayers[playerId] = obj;
        // 목표 위치도 현재 위치로 시작합니다.
        targetPositions[playerId] = pos;
        // 생성 기록을 Console 창에 남깁니다.
        Debug.Log("[상대 입장] 플레이어 " + playerId + " 캐릭터를 생성했습니다. 위치 = " + pos);
        // 다른 스크립트에게 "새 캐릭터가 생겼어요"라고 알립니다.
        OnOtherPlayerEntered?.Invoke(playerId);
        // 만들어진 캐릭터를 돌려줍니다.
        return obj;
    }

    // ===== 상대 캐릭터 삭제 =====
    private void RemoveOtherPlayer(long playerId)
    {
        // 목록에서 이 캐릭터를 찾습니다.
        GameObject obj;
        // 목록에 있다면
        if (otherPlayers.TryGetValue(playerId, out obj))
        {
            // 오브젝트가 아직 살아 있다면
            if (obj != null)
            {
                // 화면에서 완전히 삭제합니다.
                Destroy(obj);
            }

            // 목록에서도 지웁니다.
            otherPlayers.Remove(playerId);
            // 목표 위치 기록도 지웁니다.
            targetPositions.Remove(playerId);
            // 삭제 기록을 Console 창에 남깁니다.
            Debug.Log("[상대 퇴장] 플레이어 " + playerId + " 캐릭터를 삭제했습니다.");
            // 다른 스크립트에게 "캐릭터가 사라졌어요"라고 알립니다.
            OnOtherPlayerLeft?.Invoke(playerId);
        }
    }

    // ===== 내 이동 정보를 서버로 보내기 =====
    // 다른 스크립트에서도 NetworkManager.Instance.SendMove(좌표) 로 호출할 수 있습니다.
    // 서버는 이 좌표를 방의 "칠판"에 적어 두었다가 0.1초 주기로 방 전체에 묶음 배송합니다.
    public async void SendMove(Vector3 position)
    {
        // 웹소켓이 없거나 아직 열리지 않았다면 (재접속 중 포함) 보낼 수 없습니다.
        if (!IsConnected)
        {
            // 경고를 남기고
            Debug.LogWarning("[이동 전송 실패] 아직 서버와 연결되지 않았습니다.");
            // 여기서 함수를 끝냅니다.
            return;
        }

        // 마지막 전송 시각을 지금으로 갱신합니다.
        lastSendTime = Time.time;

        // 서버에 보낼 이동 동기화 양식을 만들고 내용을 채웁니다.
        MoveSync move = new MoveSync
        {
            PlayerId = MyPlayerId, // 내 플레이어 번호를 담습니다. (서버가 검증된 번호로 다시 판단하므로 참고용입니다)
            Position = new GameProtocol.Vector3 // 3D 좌표 양식(패킷용)을 만들어 담습니다.
            {
                X = position.x, // 좌우 위치입니다.
                Y = position.y, // 높이입니다.
                Z = position.z  // 앞뒤 위치입니다.
            }
        };

        // 양식을 전송 가능한 바이너리 데이터로 "포장"(직렬화)합니다.
        byte[] data = move.ToByteArray();

        // 전송 도중 연결이 끊겨도 게임이 멈추지 않도록 감쌉니다.
        try
        {
            // 포장한 데이터를 웹소켓으로 서버에 보냅니다. (바이너리 형태)
            await websocket.Send(data);
        }
        // 전송 중 오류가 났다면
        catch (Exception e)
        {
            // 경고를 남깁니다. (끊김이 확인되면 OnClose 가 재접속을 시작합니다)
            Debug.LogWarning("[이동 전송 실패] " + e.Message);
        }
    }


    void OnApplicationQuit() { isQuitting = true; }

    async void OnDestroy()
    {
        isQuitting = true;

        // 웹소켓이 만들어져 있다면
        if (websocket != null)
        {
            // 닫는 도중 오류가 나도 무시하도록 감쌉니다.
            try
            {
                // 서버와의 연결을 정상 종료 코드로 깔끔하게 닫습니다. (서버는 이 사람을 유예 없이 즉시 퇴장 처리합니다)
                await websocket.Close();
            }
            // 오류가 나더라도
            catch (Exception)
            {
                // 종료 중이므로 무시합니다.
            }
        }
    }
}