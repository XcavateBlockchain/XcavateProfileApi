using NUnit.Framework;
using Substrate.NetApi;
using Substrate.NetApi.Model.Types;
using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using XcavateProfile.Client;
using XcavateProfileApiClient;

namespace XcavateProfile.ApiTests;

[TestFixture]
public class ProfileApiTests
{
    private string mnemonic = TestMnemonics.BaseMnemonic;
    // Valid BIP39 mnemonic that produces a different, non-admin account
    // (for invalid signature / unauthorized access tests)
    private string invalidMnemonic = TestMnemonics.User3Mnemonic;

    // Address must match the account derived from BaseMnemonic, or the server
    // rejects with "Can only create profile for authenticated address"
    private string address = MnemonicsModel.GetAccountFromMnemonics(TestMnemonics.BaseMnemonic).Value;

    private string x25519Key = "0x1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef";
    private string x25519Key2 = "0x4444444444444444444444444444444444444444444444444444444444444444";


    private XcavateProfileClient? _client;
    private HttpClient? _httpClient;
    private const string TestApiUrl = "http://localhost:5000";

    [SetUp]
    public void Setup()
    {
        _httpClient = new HttpClient { BaseAddress = new Uri(TestApiUrl) };
        _client = new XcavateProfileClient(new XcavateProfileClientOptions
        {
            ApiUrl = TestApiUrl
        });
    }

    [TearDown]
    public void TearDown()
    {
        _client?.Dispose();
        _httpClient?.Dispose();
    }

    /// <summary>
    /// The API uses a persistent database, so a profile created by a previous
    /// test (or a previous test run) survives. Delete the account's own profile
    /// if it exists so every test starts from a clean state.
    /// </summary>
    private static async Task EnsureNoProfileAsync(XcavateProfileClient client, Account account)
    {
        try
        {
            await client.DeleteProfileAsync(account.Value, account);
        }
        catch (HttpRequestException)
        {
            // 404 - no profile existed, nothing to clean up
        }
    }

    #region Profile CRUD Tests

    [Test]
    public async Task Create_Profile_SuccessAsync()
    {
        var account = MnemonicsModel.GetAccountFromMnemonics(mnemonic);
        var profile = new Profile
        {
            Ss58Address = address,
            Nickname = "testuser",
            Bio = "Test profile bio",
            X25519Key = x25519Key
        };

        _client = new XcavateProfileClient(new XcavateProfileClientOptions
        {
            ApiUrl = TestApiUrl
        });
        await EnsureNoProfileAsync(_client, account);

        // Act
        var result = await _client.CreateProfileAsync(profile, account);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Ss58Address, Is.EqualTo(address));
        Assert.That(result.Nickname, Is.EqualTo("testuser"));
        Assert.That(result.Bio, Is.EqualTo("Test profile bio"));
    }

    [Test]
    public async Task Get_Profile_By_Address_SuccessAsync()
    {
        var account = MnemonicsModel.GetAccountFromMnemonics(mnemonic);
        _client = new XcavateProfileClient(new XcavateProfileClientOptions
        {
            ApiUrl = TestApiUrl
        });
        await EnsureNoProfileAsync(_client, account);

        var profile = new Profile
        {
            Ss58Address = address,
            Nickname = "getbyaddrtest",
            X25519Key = x25519Key
        };
        await _client.CreateProfileAsync(profile, account);

        // Act
        var result = await _client.GetProfileAsync(address);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Ss58Address, Is.EqualTo(address));
        Assert.That(result.Nickname, Is.EqualTo("getbyaddrtest"));
    }

    [Test]
    public async Task Get_Profile_By_Nickname_SuccessAsync()
    {
        var account = MnemonicsModel.GetAccountFromMnemonics(mnemonic);
        _client = new XcavateProfileClient(new XcavateProfileClientOptions
        {
            ApiUrl = TestApiUrl
        });
        await EnsureNoProfileAsync(_client, account);

        var profile = new Profile
        {
            Ss58Address = address,
            Nickname = "uniquenickname",
            X25519Key = x25519Key
        };
        await _client.CreateProfileAsync(profile, account);

        // Act
        var result = await _client.GetProfileByNicknameAsync("uniquenickname");

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Nickname, Is.EqualTo("uniquenickname"));
    }

    [Test]
    public async Task Update_Profile_SuccessAsync()
    {
        var account = MnemonicsModel.GetAccountFromMnemonics(mnemonic);
        _client = new XcavateProfileClient(new XcavateProfileClientOptions
        {
            ApiUrl = TestApiUrl
        });
        await EnsureNoProfileAsync(_client, account);

        // Create profile first
        var profile = new Profile
        {
            Ss58Address = address,
            Nickname = "original",
            X25519Key = x25519Key
        };
        await _client.CreateProfileAsync(profile, account);

        // Update the profile
        var updateProfile = new Profile
        {
            Ss58Address = address,
            Nickname = "updatednickname",
            Bio = "Updated bio",
            X25519Key = x25519Key
        };

        // Act
        var result = await _client.UpdateProfileAsync(address, updateProfile, account);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Nickname, Is.EqualTo("updatednickname"));
        Assert.That(result.Bio, Is.EqualTo("Updated bio"));
    }

    [Test]
    public async Task Delete_Profile_SuccessAsync()
    {
        var account = MnemonicsModel.GetAccountFromMnemonics(mnemonic);
        _client = new XcavateProfileClient(new XcavateProfileClientOptions
        {
            ApiUrl = TestApiUrl
        });
        await EnsureNoProfileAsync(_client, account);

        // Create profile first
        var profile = new Profile
        {
            Ss58Address = address,
            Nickname = "todelete",
            X25519Key = x25519Key
        };
        await _client.CreateProfileAsync(profile, account);

        // Act
        await _client.DeleteProfileAsync(address, account);

        // Assert - verify profile is gone
        var result = await _client.GetProfileAsync(address);
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Update_NonExistent_Profile_Creates_ItAsync()
    {
        // Arrange - an account with no existing profile
        var account = MnemonicsModel.GetAccountFromMnemonics(TestMnemonics.ZeroMnemonic);
        var address = account.Value;
        var x25519Key = "0xeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

        var client = new XcavateProfileClient(new XcavateProfileClientOptions
        {
            ApiUrl = TestApiUrl
        });
        await EnsureNoProfileAsync(client, account);

        var profile = new Profile
        {
            Ss58Address = address,
            Nickname = "upsertuser",
            Bio = "Created via PUT",
            X25519Key = x25519Key
        };

        // Act - PUT without a prior POST must create the profile
        var result = await client.UpdateProfileAsync(address, profile, account);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Ss58Address, Is.EqualTo(address));
        Assert.That(result.Nickname, Is.EqualTo("upsertuser"));
        Assert.That(result.Bio, Is.EqualTo("Created via PUT"));

        // The profile must actually be persisted
        var fetched = await client.GetProfileAsync(address);
        Assert.That(fetched, Is.Not.Null);
        Assert.That(fetched!.Nickname, Is.EqualTo("upsertuser"));
    }

    #endregion

    #region Authentication Tests

    [Test]
    public async Task Create_Profile_Fails_With_Invalid_SignatureAsync()
    {
        // Arrange
        var x25519Key = "0x1111111111111111111111111111111111111111111111111111111111111111";
        var profile = new Profile
        {
            Ss58Address = "5GrwvaEF5zKbXCEe9qGjZL23Y641mot2Ff6hS3s8jF3g3k3Z",
            X25519Key = x25519Key
        };

        // The signing account does not match the profile's address, so the
        // server must reject the request as unauthorized
        var invalidAccount = MnemonicsModel.GetAccountFromMnemonics(invalidMnemonic);

        _client = new XcavateProfileClient(new XcavateProfileClientOptions
        {
            ApiUrl = TestApiUrl
        });

        // Act & Assert
        var ex = Assert.ThrowsAsync<HttpRequestException>(() => _client.CreateProfileAsync(profile, invalidAccount));
        Assert.That(ex?.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Create_Profile_Fails_With_Expired_TimestampAsync()
    {
        var account = MnemonicsModel.GetAccountFromMnemonics(mnemonic);
        var profile = new Profile { Ss58Address = address, X25519Key = x25519Key };

        _client = new XcavateProfileClient(new XcavateProfileClientOptions
        {
            ApiUrl = TestApiUrl
        });

        var bodyJson = JsonSerializer.Serialize(profile);

        // Manually create a request with old timestamp (simulate replay attack)
        var oldTimestamp = DateTime.UtcNow.AddMinutes(-10);
        var payload = CryptoHelper.ConstructPayload("POST", "/api/profiles", profile, oldTimestamp);

        var signature = await CryptoHelper.SignAsync(payload, account);

        // Build request manually with old timestamp
        var content = new StringContent(bodyJson, System.Text.Encoding.UTF8, "application/json");
        content.Headers.Add("X-SS58-Address", account.Value);
        content.Headers.Add("X-Signature", Utils.Bytes2HexString(signature));
        content.Headers.Add("X-Timestamp", oldTimestamp.ToString("o"));

        var request = new HttpRequestMessage(HttpMethod.Post, "api/profiles")
        {
            Content = content
        };

        // Act & Assert
        var response = await _httpClient!.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Update_AnotherUserProfile_FailsAsync()
    {
        // Arrange - user1 creates a profile
        var account = MnemonicsModel.GetAccountFromMnemonics(mnemonic);
        var client1 = new XcavateProfileClient(new XcavateProfileClientOptions
        {
            ApiUrl = TestApiUrl
        });
        await EnsureNoProfileAsync(client1, account);

        var profile1 = new Profile { Ss58Address = address, Nickname = "user1", X25519Key = x25519Key };
        await client1.CreateProfileAsync(profile1, account);

        // A different, non-admin account tries to update user1's profile
        var attackerAccount = MnemonicsModel.GetAccountFromMnemonics(invalidMnemonic);

        var client2 = new XcavateProfileClient(new XcavateProfileClientOptions
        {
            ApiUrl = TestApiUrl
        });

        var updateProfile = new Profile { Ss58Address = address, Nickname = "hacked", X25519Key = x25519Key2 };

        // Act & Assert
        var ex = Assert.ThrowsAsync<HttpRequestException>(() => client2.UpdateProfileAsync(address, updateProfile, attackerAccount));
        Assert.That(ex?.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task Delete_AnotherUserProfile_FailsAsync()
    {
        // User 1 profile creation - use a valid mnemonic that produces different account
        var user1Mnemonic = TestMnemonics.User1Mnemonic;
        var user1Account = MnemonicsModel.GetAccountFromMnemonics(user1Mnemonic);
        var user1Address = user1Account.Value;
        var user1Client = new XcavateProfileClient(new XcavateProfileClientOptions
        {
            ApiUrl = TestApiUrl
        });
        await EnsureNoProfileAsync(user1Client, user1Account);

        var profile1 = new Profile { Ss58Address = user1Address, Nickname = "user1delete", X25519Key = x25519Key };
        await user1Client.CreateProfileAsync(profile1, user1Account);

        // User 2 tries to delete User 1's profile (should fail)
        // Use a valid but different mnemonic
        var user2Mnemonic = TestMnemonics.User2Mnemonic;
        var user2Account = MnemonicsModel.GetAccountFromMnemonics(user2Mnemonic);
        var client2 = new XcavateProfileClient(new XcavateProfileClientOptions
        {
            ApiUrl = TestApiUrl
        });

        var ex = Assert.ThrowsAsync<HttpRequestException>(() => client2.DeleteProfileAsync(user1Address, user2Account));
        Assert.That(ex?.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.Forbidden));
    }

    #endregion

    #region Admin Authorization Tests

    [Test]
    public async Task Admin_Can_Update_OtherUserProfileAsync()
    {
        // Arrange - First create admin profile, then regular user profile
        var adminMnemonic = TestMnemonics.AdminMnemonic;
        var adminAccount = MnemonicsModel.GetAccountFromMnemonics(adminMnemonic);
        var adminAddress = adminAccount.Value;
        var adminClient = new XcavateProfileClient(new XcavateProfileClientOptions
        {
            ApiUrl = TestApiUrl
        });
        await EnsureNoProfileAsync(adminClient, adminAccount);

        var adminProfile = new Profile { Ss58Address = adminAddress, Nickname = "admin", X25519Key = x25519Key };
        await adminClient.CreateProfileAsync(adminProfile, adminAccount);

        var userMnemonic = TestMnemonics.UserMnemonic;
        var userAccount = MnemonicsModel.GetAccountFromMnemonics(userMnemonic);
        var userAddress = userAccount.Value;
        var userClient = new XcavateProfileClient(new XcavateProfileClientOptions
        {
            ApiUrl = TestApiUrl
        });
        await EnsureNoProfileAsync(userClient, userAccount);

        var userProfile = new Profile { Ss58Address = userAddress, Nickname = "regularuser", X25519Key = x25519Key };
        await userClient.CreateProfileAsync(userProfile, userAccount);

        // Admin updates regular user's profile
        var updatedProfile = new Profile { Ss58Address = userAddress, Nickname = "regularuser_updated", Bio = "Updated by admin", X25519Key = x25519Key };

        // Act
        var result = await adminClient.UpdateProfileAsync(userAddress, updatedProfile, adminAccount);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Nickname, Is.EqualTo("regularuser_updated"));
    }

    [Test]
    public async Task Admin_Can_Delete_OtherUserProfileAsync()
    {
        // Arrange - a regular user creates a profile
        var userAccount = MnemonicsModel.GetAccountFromMnemonics(TestMnemonics.User4Mnemonic);
        var userAddress = userAccount.Value;
        var userClient = new XcavateProfileClient(new XcavateProfileClientOptions
        {
            ApiUrl = TestApiUrl
        });
        await EnsureNoProfileAsync(userClient, userAccount);

        var userProfile = new Profile { Ss58Address = userAddress, Nickname = "regularuser2", X25519Key = x25519Key };
        await userClient.CreateProfileAsync(userProfile, userAccount);

        // Admin deletes regular user's profile
        var adminAccount = MnemonicsModel.GetAccountFromMnemonics(TestMnemonics.AdminMnemonic);
        var adminClient = new XcavateProfileClient(new XcavateProfileClientOptions
        {
            ApiUrl = TestApiUrl
        });

        // Act
        await adminClient.DeleteProfileAsync(userAddress, adminAccount);

        // Assert
        var result = await adminClient.GetProfileAsync(userAddress);
        Assert.That(result, Is.Null);
    }

    #endregion

    #region Nickname Uniqueness Test

    [Test]
    public async Task Nickname_Uniqueness_Constraint_EnforcedAsync()
    {
        // Arrange
        var user1Account = MnemonicsModel.GetAccountFromMnemonics(mnemonic);
        var address1 = user1Account.Value;
        var client1 = new XcavateProfileClient(new XcavateProfileClientOptions
        {
            ApiUrl = TestApiUrl
        });
        await EnsureNoProfileAsync(client1, user1Account);

        var profile1 = new Profile { Ss58Address = address1, Nickname = "uniqueNick", X25519Key = x25519Key };
        await client1.CreateProfileAsync(profile1, user1Account);

        // Act - use a valid but different mnemonic for user2
        var user2Mnemonic = TestMnemonics.Nick2Mnemonic;
        var user2Account = MnemonicsModel.GetAccountFromMnemonics(user2Mnemonic);
        var client2 = new XcavateProfileClient(new XcavateProfileClientOptions
        {
            ApiUrl = TestApiUrl
        });
        await EnsureNoProfileAsync(client2, user2Account);

        // user2 tries to register the nickname user1 already owns
        var profile2 = new Profile { Ss58Address = user2Account.Value, Nickname = "uniqueNick", X25519Key = x25519Key };

        // Assert
        var ex = Assert.ThrowsAsync<HttpRequestException>(() => client2.CreateProfileAsync(profile2, user2Account));
        Assert.That(ex?.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.BadRequest));
    }

    /// <summary>
    /// Nicknames are one name in whatever case they are written: the second user cannot register the
    /// first user's nickname by recasing it, and either spelling looks the profile up. This is the
    /// suite that runs it against PostgreSQL, where the unique index on the folded nickname lives.
    /// </summary>
    [Test]
    public async Task Nickname_Uniqueness_Ignores_CaseAsync()
    {
        // Arrange
        var ownerAccount = MnemonicsModel.GetAccountFromMnemonics(TestMnemonics.NickMnemonic);
        var owner = new XcavateProfileClient(new XcavateProfileClientOptions
        {
            ApiUrl = TestApiUrl
        });
        await EnsureNoProfileAsync(owner, ownerAccount);

        var strangerAccount = MnemonicsModel.GetAccountFromMnemonics(TestMnemonics.Nick3Mnemonic);
        var stranger = new XcavateProfileClient(new XcavateProfileClientOptions
        {
            ApiUrl = TestApiUrl
        });
        await EnsureNoProfileAsync(stranger, strangerAccount);

        var owned = new Profile
        {
            Ss58Address = ownerAccount.Value,
            Nickname = "CaseNick",
            X25519Key = x25519Key
        };
        await owner.CreateProfileAsync(owned, ownerAccount);

        // Act
        var foundByOtherCase = await owner.GetProfileByNicknameAsync("casenick");

        var claimed = new Profile
        {
            Ss58Address = strangerAccount.Value,
            Nickname = "casenick",
            X25519Key = x25519Key
        };
        var ex = Assert.ThrowsAsync<HttpRequestException>(
            () => stranger.CreateProfileAsync(claimed, strangerAccount));

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(foundByOtherCase, Is.Not.Null, "the lookup ignores case");
            Assert.That(
                foundByOtherCase!.Ss58Address, Is.EqualTo(ownerAccount.Value));
            Assert.That(
                foundByOtherCase.Nickname, Is.EqualTo("CaseNick"),
                "the stored nickname keeps the case it was written in");
            Assert.That(ex?.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.BadRequest));
        });
    }

    #endregion

    #region Image Upload Test

    [Test]
    public async Task Upload_Profile_Image_SuccessAsync()
    {
        // Arrange
        var mnemonic = TestMnemonics.Image2Mnemonic;
        var account = MnemonicsModel.GetAccountFromMnemonics(mnemonic);
        var address = account.Value;
        var x25519Key = "0xcccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

        var client = new XcavateProfileClient(new XcavateProfileClientOptions
        {
            ApiUrl = TestApiUrl
        });
        await EnsureNoProfileAsync(client, account);

        var profile = new Profile { Ss58Address = address, Nickname = "imageuser", X25519Key = x25519Key };
        await client.CreateProfileAsync(profile, account);

        // Create a dummy image stream
        var dummyImageBytes = System.Text.Encoding.UTF8.GetBytes("fake image content");
        var imageStream = new MemoryStream(dummyImageBytes);

        // Act
        var imageUrl = await client.UploadImageAsync(address, imageStream, "test.jpg", account);

        // Assert
        Assert.That(imageUrl, Is.Not.Null);
        Assert.That(imageUrl!.Contains("test.jpg"), Is.True);
    }

    [Test]
    public async Task Upload_Same_Filename_Twice_Overwrites_Existing_ImageAsync()
    {
        // Arrange
        var mnemonic = TestMnemonics.Image4Mnemonic;
        var account = MnemonicsModel.GetAccountFromMnemonics(mnemonic);
        var address = account.Value;
        var x25519Key = "0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

        var client = new XcavateProfileClient(new XcavateProfileClientOptions
        {
            ApiUrl = TestApiUrl
        });
        await EnsureNoProfileAsync(client, account);

        var profile = new Profile { Ss58Address = address, Nickname = "overwriteuser", X25519Key = x25519Key };
        await client.CreateProfileAsync(profile, account);

        // Act - upload two different images under the same filename
        var firstStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("first image content"));
        var firstUrl = await client.UploadImageAsync(address, firstStream, "overwrite.jpg", account);

        var secondStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("second image content"));
        var secondUrl = await client.UploadImageAsync(address, secondStream, "overwrite.jpg", account);

        // Assert - same filename maps to the same object, so the second upload
        // rewrites the first instead of creating a new one
        Assert.That(firstUrl, Does.Contain("overwrite.jpg"));
        Assert.That(secondUrl, Is.EqualTo(firstUrl));
    }

    [Test]
    public async Task Upload_NonImage_File_Is_RejectedAsync()
    {
        // Arrange - the bucket serves objects publicly, so storing an HTML file
        // with a text/html content type would enable stored XSS / phishing.
        // The server must reject anything that is not a known image type.
        var mnemonic = TestMnemonics.Image5Mnemonic;
        var account = MnemonicsModel.GetAccountFromMnemonics(mnemonic);
        var address = account.Value;
        var x25519Key = "0xabababababababababababababababababababababababababababababababab";

        var client = new XcavateProfileClient(new XcavateProfileClientOptions
        {
            ApiUrl = TestApiUrl
        });
        await EnsureNoProfileAsync(client, account);

        var profile = new Profile { Ss58Address = address, Nickname = "evilfileuser", X25519Key = x25519Key };
        await client.CreateProfileAsync(profile, account);

        var timestamp = DateTime.UtcNow;
        var payload = CryptoHelper.ConstructPayload("POST", $"/api/profiles/{address}/image", new EmptyPayloadBody(), timestamp);
        var signature = await CryptoHelper.SignAsync(payload, account);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/profiles/{address}/image");
        request.Headers.Add("X-SS58-Address", address);
        request.Headers.Add("X-Signature", Utils.Bytes2HexString(signature));
        request.Headers.Add("X-Timestamp", timestamp.ToUniversalTime().ToString("o"));

        var htmlPart = new StreamContent(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("<script>alert(1)</script>")));
        htmlPart.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html");
        var content = new MultipartFormDataContent();
        content.Add(htmlPart, "image", "evil.html");
        request.Content = content;

        // Act
        var response = await _httpClient!.SendAsync(request);

        // Assert
        Assert.That(response.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Upload_Large_Profile_Image_25MB_SuccessAsync()
    {
        // Arrange
        var mnemonic = TestMnemonics.ImageMnemonic;
        var account = MnemonicsModel.GetAccountFromMnemonics(mnemonic);
        var address = account.Value;
        var x25519Key = "0xdddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";

        var client = new XcavateProfileClient(new XcavateProfileClientOptions
        {
            ApiUrl = TestApiUrl
        });
        await EnsureNoProfileAsync(client, account);

        var profile = new Profile { Ss58Address = address, Nickname = "largeimageuser", X25519Key = x25519Key };
        await client.CreateProfileAsync(profile, account);

        // Images up to 25MB must be accepted
        var imageStream = new MemoryStream(new byte[25 * 1024 * 1024]);

        // Act
        var imageUrl = await client.UploadImageAsync(address, imageStream, "large.jpg", account);

        // Assert
        Assert.That(imageUrl, Is.Not.Null);
        Assert.That(imageUrl!.Contains("large.jpg"), Is.True);
    }

    [Test]
    public async Task Upload_Profile_Image_Without_ContentType_SuccessAsync()
    {
        // Arrange - some clients omit the Content-Type header on the multipart file
        // part; the server must fall back to a sensible type instead of failing
        var mnemonic = TestMnemonics.Image3Mnemonic;
        var account = MnemonicsModel.GetAccountFromMnemonics(mnemonic);
        var uploadAddress = account.Value;
        var x25519Key = "0xeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

        var client = new XcavateProfileClient(new XcavateProfileClientOptions
        {
            ApiUrl = TestApiUrl
        });
        await EnsureNoProfileAsync(client, account);

        var profile = new Profile { Ss58Address = uploadAddress, Nickname = "noctypeuser", X25519Key = x25519Key };
        await client.CreateProfileAsync(profile, account);

        // Build the multipart request by hand so the file part has NO Content-Type
        var timestamp = DateTime.UtcNow;
        var payload = CryptoHelper.ConstructPayload("POST", $"/api/profiles/{uploadAddress}/image", new EmptyPayloadBody(), timestamp);
        var signature = await CryptoHelper.SignAsync(payload, account);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/profiles/{uploadAddress}/image");
        request.Headers.Add("X-SS58-Address", uploadAddress);
        request.Headers.Add("X-Signature", Utils.Bytes2HexString(signature));
        request.Headers.Add("X-Timestamp", timestamp.ToUniversalTime().ToString("o"));

        var content = new MultipartFormDataContent();
        content.Add(new StreamContent(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("fake image content"))), "image", "noctype.jpg");
        request.Content = content;

        // Act
        var response = await _httpClient!.SendAsync(request);

        // Assert
        Assert.That(response.IsSuccessStatusCode, Is.True, $"Expected success but got {(int)response.StatusCode}");
        var imageUrl = await response.Content.ReadAsStringAsync();
        Assert.That(imageUrl, Does.Contain("noctype.jpg"));
    }

    #endregion

    #region Swagger Test

    [Test]
    public async Task Swagger_Document_Is_GeneratedAsync()
    {
        var response = await _httpClient!.GetAsync("/swagger/v1/swagger.json");

        Assert.That(response.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.OK));

        var json = await response.Content.ReadAsStringAsync();
        Assert.That(json, Does.Contain("\"openapi\""));
    }

    #endregion
}
