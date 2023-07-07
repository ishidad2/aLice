using System.Text;
using System.Text.Json;
using CatSdk.CryptoTypes;
using CatSdk.Symbol;
using CatSdk.Utils;

namespace aLice;

public partial class RequestSignBatches : ContentPage
{
    private List<string> data;
    private string callbackUrl;
    private SavedAccounts savedAccounts;
    private readonly string method;
    private readonly string redirectUrl;
    private SavedAccount mainAccount;
    private readonly List<(IBaseTransaction transaction, string parsedTransaction)> parsedTransaction;
    
    public RequestSignBatches(List<string> _data, string _callbackUrl, string _method, string _redirectUrl = null)
    {
        InitializeComponent();
        data = _data;
        redirectUrl = _redirectUrl;
        callbackUrl = _callbackUrl;
        method = _method;
        parsedTransaction = new List<(IBaseTransaction transaction, string parsedTransaction)>();
    }
    
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await ShowRequestSign();
    }

    // 署名を要求されたときに呼び出される
    private async Task ShowRequestSign()
    {
        try
        {
            var uri = new Uri(callbackUrl);
            var baseUrl = $"{uri.Scheme}://{uri.Authority}";
            Domain.Text = $"{baseUrl}からの署名要求です";
            
            var accounts = await SecureStorage.GetAsync("accounts");
            savedAccounts = JsonSerializer.Deserialize<SavedAccounts>(accounts);
            if (savedAccounts.accounts[0] == null) throw new NullReferenceException("アカウントが登録されていません");
            mainAccount = savedAccounts.accounts.Find((acc) => acc.isMain);
            foreach (var s in data)
            {
                var tx = SymbolTransaction.ParseEmbeddedTransaction(s);
                tx.transaction.SignerPublicKey = new PublicKey(Converter.HexToBytes(mainAccount.publicKey));
                Data.Text += tx.parsedTransaction;
                parsedTransaction.Add(tx);
            }
            Type.Text = "複数のトランザクションです";
            Ask.Text = $"{mainAccount.accountName}で署名しますか？";
        }
        catch (Exception exception)
        {
            Error.Text = exception.Message;
        }
    }
    
    // 署名を受け入れたときに呼び出される
    private async void AcceptRequestSign(object sender, EventArgs e)
    {
        Error.Text = "";
        try
        {
            var password = await DisplayPromptAsync("Password", "パスワードを入力してください", "Sign", "Cancel", "Input Password", -1, Keyboard.Numeric);
            if (password == null) return;
            string privateKey;
            try {
                privateKey = CatSdk.Crypto.Crypto.DecryptString(mainAccount.encryptedPrivateKey, password, mainAccount.address);
            }
            catch {
                throw new Exception("パスワードが正しくありません");
            }

            var keyPair = new KeyPair(new PrivateKey(privateKey));
            var network = parsedTransaction[0].transaction.Network == CatSdk.Symbol.NetworkType.MAINNET ? CatSdk.Symbol.Network.MainNet : CatSdk.Symbol.Network.TestNet;
            var metal = new Metal(network);
            var txs = parsedTransaction.Select(valueTuple => valueTuple.transaction).ToList();
            
            var aggs = metal.SignedAggregateCompleteTxBatches(txs, keyPair, network);
            switch (method)
            {
                case "post":
                {
                    var dic = new Dictionary<string, string> {{"pubkey", mainAccount.publicKey}};
                    for (var i = 0; i < aggs.Count; i++)
                    {
                        dic.Add("signed" + i, Converter.BytesToHex(aggs[i].Serialize()));   
                    }
                    using var client = new HttpClient();
                    var content = new StringContent(JsonSerializer.Serialize(dic), Encoding.UTF8, "application/json");
                    var response =  client.PostAsync(callbackUrl, content).Result;
                    await response.Content.ReadAsStringAsync();
                    if (redirectUrl != null) await Launcher.OpenAsync(new Uri(redirectUrl));
                    break;
                }
                case "get":
                {
                    var additionalParam =
                        $"pubkey={mainAccount.publicKey}&original_data={data}";
                    if (callbackUrl.Contains('?')) {
                        callbackUrl += "&" + additionalParam;
                    }
                    else {
                        callbackUrl += "?" + additionalParam;
                    }
                    for (var i = 0; i < aggs.Count; i++)
                    {
                        var signedPayload = Converter.BytesToHex(aggs[i].Serialize());
                        callbackUrl += $"&signed{i}={signedPayload}";
                    }
                    await Launcher.OpenAsync(new Uri(callbackUrl));
                    break;
                }
                default:
                    throw new Exception("不正なリクエストです");
            }

            Reset();
            await Navigation.PopModalAsync();
        } 
        catch (Exception exception)
        {
            Error.Text = exception.Message;
        }
    }
    
    private async void ChangeAccount(object sender, EventArgs e)
    {
        var accountNames = new string[savedAccounts.accounts.Count];
        for (var i = 0; i < savedAccounts.accounts.Count; i++)
        {
            accountNames[i] = savedAccounts.accounts[i].accountName;
        }
        var accName = await DisplayActionSheet("アカウント切り替え", "cancel", null, accountNames);
        mainAccount = savedAccounts.accounts.Find(acc => acc.accountName == accName);
        Ask.Text = $"{mainAccount.accountName}で署名しますか？";
    }
    
    // 署名を拒否したときに呼び出される
    private async void RejectedRequestSign(object sender, EventArgs e)
    {
        const string additionalParam = "error=sign_rejected";
        if (callbackUrl.Contains('?')) {
            callbackUrl += "&" + additionalParam;
        }
        else {
            callbackUrl += "?" + additionalParam;
        }
        await Launcher.OpenAsync(new Uri(callbackUrl));
        Reset();
        await Navigation.PopModalAsync();
    }

    private void Reset()
    {
        callbackUrl = null;
        data = null;
        mainAccount = null;
    }
}