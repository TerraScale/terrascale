package sdk

import (
	"encoding/json"
	"fmt"
	"math"
	"strings"
	"testing"
)

// Exact raw bodies emitted by the accepted protocol8 loopback HTTP fixture.
// Manifest d00d65a3d7e78b43d10de4e33d95debd7a1b3eeef53b7c5280b204c5441239f3.
// Accounts and credentials are synthetic; no native/hosted authority is claimed.
var capturedResourceResponses = []struct {
	name            string
	status          int
	body, sourceSHA string
}{
	{"client-access-introspection", 200, "{\"active\":true,\"token_type\":\"Bearer\",\"iss\":\"http://localhost:36701/resources\",\"sub\":\"ab0357f4-db70-4ddd-94fa-95255ceb3317\",\"aud\":\"JuDmfb6pJsPF8l97tTDUIEQr\",\"client_id\":\"JuDmfb6pJsPF8l97tTDUIEQr\",\"scope\":\"openid email\",\"exp\":1790751957,\"iat\":1790748357,\"sid\":\"4fea65ec-3bff-408d-9b0e-b8ec2ab97af6\",\"principal_kind\":\"user\"}", "9aa38e54ac2302e702b09d4fdaae5a815e1686c323ad314726f7c4303e4780ae"},
	{"client-create", 200, "{\"client\":{\"id\":\"fd07711b-4cdd-459a-9e4f-44d6e7ac17b8\",\"client_id\":\"O7WimIKtyVxcispIMQ8HWpud\",\"name\":\"Resource client\",\"redirect_uris\":[\"https://client.example.test/resource-callback\"],\"post_logout_redirect_uris\":[],\"token_endpoint_auth_method\":\"client_secret_basic\",\"public\":false,\"allowed_origins\":[],\"allow_anonymous\":false,\"created_at\":1790748356,\"allowed_resource_audiences\":[\"https://api.example.test/management\",\"https://api.example.test/documents\"],\"allowed_scopes\":[\"openid\",\"email\",\"profile\",\"management:read\",\"management:write\"],\"allowed_grant_types\":[\"authorization_code\",\"refresh_token\",\"client_credentials\",\"urn:ietf:params:oauth:grant-type:device_code\"],\"auth_epoch\":1,\"enabled\":true},\"client_secret\":\"CzWIzDGKooK1xwnJMItsLN6hvrrQaC6y8uso4f9Z\"}", "e97dbd898c14254f5394f634eb7ffea544441b09678c0f37fa506370999cc40a"},
	{"client-create", 200, "{\"client\":{\"id\":\"4497dc14-9dff-465f-9397-2e30bee3c35e\",\"client_id\":\"RxrjGqh3AbqzSsmSOT8Xbwvy\",\"name\":\"Resource client\",\"redirect_uris\":[\"https://client.example.test/resource-callback\"],\"post_logout_redirect_uris\":[],\"token_endpoint_auth_method\":\"client_secret_basic\",\"public\":false,\"allowed_origins\":[],\"allow_anonymous\":false,\"created_at\":1790748356,\"allowed_resource_audiences\":[\"https://api.example.test/management\",\"https://api.example.test/documents\"],\"allowed_scopes\":[\"openid\",\"email\",\"profile\",\"management:read\",\"management:write\"],\"allowed_grant_types\":[\"authorization_code\",\"refresh_token\",\"client_credentials\",\"urn:ietf:params:oauth:grant-type:device_code\"],\"auth_epoch\":1,\"enabled\":true},\"client_secret\":\"IAFnq27kG8doTGjFnG839AvJRZjzfEuB9W2iF2xi\"}", "a93a79f5b510bc890c154bee5733a1fb5b6575fac91405cf8c4fdf244e9a0a4e"},
	{"client-create", 200, "{\"client\":{\"id\":\"3edeb7c6-4d63-41b2-8c37-e71698eff985\",\"client_id\":\"o7H0V1pqjKb9IO3ltjuznoEv\",\"name\":\"Resource client\",\"redirect_uris\":[\"https://client.example.test/resource-callback\"],\"post_logout_redirect_uris\":[],\"token_endpoint_auth_method\":\"client_secret_basic\",\"public\":false,\"allowed_origins\":[],\"allow_anonymous\":false,\"created_at\":1790748356,\"allowed_resource_audiences\":[\"https://api.example.test/management\",\"https://api.example.test/documents\"],\"allowed_scopes\":[\"openid\",\"email\",\"profile\",\"management:read\",\"management:write\"],\"allowed_grant_types\":[\"authorization_code\",\"refresh_token\",\"client_credentials\",\"urn:ietf:params:oauth:grant-type:device_code\"],\"auth_epoch\":1,\"enabled\":true},\"client_secret\":\"YBFCZbWBQrqyziRD2RhlkGkLcQLToKCkReR1vQiH\"}", "d205c74df36d2fe7db4f937b7d7a9460de977b780aec9426e8f5960634bfd491"},
	{"client-create", 200, "{\"client\":{\"id\":\"77cfee80-9540-44c2-8065-99adf0bf1a52\",\"client_id\":\"JuDmfb6pJsPF8l97tTDUIEQr\",\"name\":\"Resource client\",\"redirect_uris\":[\"https://client.example.test/resource-callback\"],\"post_logout_redirect_uris\":[],\"token_endpoint_auth_method\":\"client_secret_basic\",\"public\":false,\"allowed_origins\":[],\"allow_anonymous\":false,\"created_at\":1790748356,\"allowed_resource_audiences\":[\"https://api.example.test/management\",\"https://api.example.test/documents\"],\"allowed_scopes\":[\"openid\",\"email\",\"profile\",\"management:read\",\"management:write\"],\"allowed_grant_types\":[\"authorization_code\",\"refresh_token\",\"client_credentials\",\"urn:ietf:params:oauth:grant-type:device_code\"],\"auth_epoch\":1,\"enabled\":true},\"client_secret\":\"2ra5b8CcpzcmYu7IDZQOvfVr8PdtJi4Hm0nFC9ll\"}", "8660adce082bfe7ab38d3dcc304defca64c3bf3b85fe94600b106abf4fa36298"},
	{"client-list", 200, "{\"clients\":[{\"id\":\"fd07711b-4cdd-459a-9e4f-44d6e7ac17b8\",\"client_id\":\"O7WimIKtyVxcispIMQ8HWpud\",\"name\":\"Resource client\",\"redirect_uris\":[\"https://client.example.test/resource-callback\"],\"post_logout_redirect_uris\":[],\"token_endpoint_auth_method\":\"client_secret_basic\",\"public\":false,\"allowed_origins\":[],\"allow_anonymous\":false,\"created_at\":1790748356,\"allowed_resource_audiences\":[\"https://api.example.test/management\",\"https://api.example.test/documents\"],\"allowed_scopes\":[\"openid\",\"email\",\"profile\",\"management:read\",\"management:write\"],\"allowed_grant_types\":[\"authorization_code\",\"refresh_token\",\"client_credentials\",\"urn:ietf:params:oauth:grant-type:device_code\"],\"auth_epoch\":1,\"enabled\":true}]}", "ec133bb211b01581d580d1f5bc8c7f9c6ebfbd9f49ab9c91fe72295d7d84a550"},
	{"client-refresh-introspection", 200, "{\"active\":true,\"token_type\":\"refresh_token\",\"iss\":\"http://localhost:36701/resources\",\"aud\":\"JuDmfb6pJsPF8l97tTDUIEQr\",\"sub\":\"ab0357f4-db70-4ddd-94fa-95255ceb3317\",\"client_id\":\"JuDmfb6pJsPF8l97tTDUIEQr\",\"scope\":\"openid email\",\"exp\":1793340357,\"iat\":1790748357,\"sid\":\"4fea65ec-3bff-408d-9b0e-b8ec2ab97af6\",\"family_id\":\"36a3928a-ae6f-4f02-9e8d-a93ba63add29\"}", "28bf848ab5f2952f58c549f5c5b68a335e2c10f7b6278b8e1ae774febe585418"},
	{"client-update", 200, "{\"id\":\"fd07711b-4cdd-459a-9e4f-44d6e7ac17b8\",\"client_id\":\"O7WimIKtyVxcispIMQ8HWpud\",\"name\":\"Policy changed\",\"redirect_uris\":[\"https://client.example.test/resource-callback\"],\"post_logout_redirect_uris\":[],\"token_endpoint_auth_method\":\"client_secret_basic\",\"public\":false,\"allowed_origins\":[],\"allow_anonymous\":false,\"created_at\":1790748356,\"allowed_resource_audiences\":[\"https://api.example.test/management\",\"https://api.example.test/documents\"],\"allowed_scopes\":[\"openid\",\"email\",\"profile\",\"management:read\",\"management:write\"],\"allowed_grant_types\":[\"authorization_code\",\"refresh_token\",\"client_credentials\",\"urn:ietf:params:oauth:grant-type:device_code\"],\"auth_epoch\":2,\"enabled\":true}", "5ce1ec1eadb8d1db6896610bede36dd144ec37b4d1f3aae796571c20f1e02390"},
	{"client-update-rotated", 200, "{\"client\":{\"id\":\"fd07711b-4cdd-459a-9e4f-44d6e7ac17b8\",\"client_id\":\"O7WimIKtyVxcispIMQ8HWpud\",\"name\":\"Policy changed\",\"redirect_uris\":[\"https://client.example.test/resource-callback\"],\"post_logout_redirect_uris\":[],\"token_endpoint_auth_method\":\"client_secret_basic\",\"public\":false,\"allowed_origins\":[],\"allow_anonymous\":false,\"created_at\":1790748356,\"allowed_resource_audiences\":[\"https://api.example.test/management\",\"https://api.example.test/documents\"],\"allowed_scopes\":[\"openid\",\"email\",\"profile\",\"management:read\",\"management:write\"],\"allowed_grant_types\":[\"authorization_code\",\"refresh_token\",\"client_credentials\",\"urn:ietf:params:oauth:grant-type:device_code\"],\"auth_epoch\":3,\"enabled\":true},\"client_secret\":\"yKuBDE28nGT7y9KSSiffwbL97DjPe8H0ZwPBss8Y\"}", "2b451e77ff63379d7588ae9467a81728d2db5be2e7e96d6902e42a2a7bd4116d"},
	{"resource-create", 200, "{\"resource\":{\"id\":\"652d3a14-bfef-4d2d-91e4-5926d1e613bb\",\"audience\":\"https://api.example.test/documents\",\"allowed_client_ids\":[\"O7WimIKtyVxcispIMQ8HWpud\"],\"allowed_grant_types\":[\"authorization_code\",\"refresh_token\",\"client_credentials\",\"urn:ietf:params:oauth:grant-type:device_code\"],\"allowed_scopes\":[\"openid\",\"email\",\"profile\",\"management:read\",\"management:write\"],\"introspection_client_id\":\"m5dk7618Y6pAIGwcynIa0bnRr6qrjl8E\",\"token_endpoint_auth_method\":\"client_secret_basic\",\"auth_epoch\":1,\"enabled\":true,\"created_at\":1790748356,\"updated_at\":1790748356},\"introspection_secret\":\"w4BzHKnlnhQGDYFMCZYu433u7GWY19feWrfjzjP7a4vs6GhF\"}", "0579013726099251996d47d1bb4e58e7a46e283842567b770646ee71eb582a74"},
	{"resource-create", 200, "{\"resource\":{\"id\":\"9005631a-324a-4c2c-973c-eed9c4d73a51\",\"audience\":\"https://api.example.test/documents\",\"allowed_client_ids\":[\"RxrjGqh3AbqzSsmSOT8Xbwvy\"],\"allowed_grant_types\":[\"authorization_code\",\"refresh_token\",\"client_credentials\",\"urn:ietf:params:oauth:grant-type:device_code\"],\"allowed_scopes\":[\"openid\",\"email\",\"profile\",\"management:read\",\"management:write\"],\"introspection_client_id\":\"vvoZuE93dRdfbHyaf6IWEw7TLZXkwYyP\",\"token_endpoint_auth_method\":\"client_secret_basic\",\"auth_epoch\":1,\"enabled\":true,\"created_at\":1790748356,\"updated_at\":1790748356},\"introspection_secret\":\"lo3khCRBF9rWdcE4BkwPgTFiMQ34GLcbjsLFmJgWO0T8QdPC\"}", "d12b02289fc993446a4de2bc4e0dc31e13bcfb262a6789422ca6296ce7e1a44e"},
	{"resource-create", 200, "{\"resource\":{\"id\":\"31645e62-22ab-4bd7-848c-1683a51699d8\",\"audience\":\"https://api.example.test/documents\",\"allowed_client_ids\":[\"JuDmfb6pJsPF8l97tTDUIEQr\"],\"allowed_grant_types\":[\"authorization_code\",\"refresh_token\",\"client_credentials\",\"urn:ietf:params:oauth:grant-type:device_code\"],\"allowed_scopes\":[\"openid\",\"email\",\"profile\",\"management:read\",\"management:write\"],\"introspection_client_id\":\"sabdfg70i7FHmAiLRyBcGfqu2faeamGQ\",\"token_endpoint_auth_method\":\"client_secret_basic\",\"auth_epoch\":1,\"enabled\":true,\"created_at\":1790748356,\"updated_at\":1790748356},\"introspection_secret\":\"jZovSCIZP3qID3tFPTNVYNVeYBdR1uyz6hhhKpMwe5l9aPgD\"}", "6b9901f6c083e298a965d33d7acd28ab690c8a7bfde8cced9cf2a170d795cb37"},
	{"resource-create", 200, "{\"resource\":{\"id\":\"41c68fda-d0c5-495e-bde2-94c8d6d440ce\",\"audience\":\"https://api.example.test/management\",\"allowed_client_ids\":[\"o7H0V1pqjKb9IO3ltjuznoEv\"],\"allowed_grant_types\":[\"authorization_code\",\"refresh_token\",\"client_credentials\",\"urn:ietf:params:oauth:grant-type:device_code\"],\"allowed_scopes\":[\"openid\",\"email\",\"profile\",\"management:read\",\"management:write\"],\"introspection_client_id\":\"Bu2JUhwFFueQ9ynabuyKONPyuGkLYU9e\",\"token_endpoint_auth_method\":\"client_secret_basic\",\"auth_epoch\":1,\"enabled\":true,\"created_at\":1790748356,\"updated_at\":1790748356},\"introspection_secret\":\"BsGPbs5fVe5yDoAxJfwib5VS9DmWYLGijrdIGz4Mwbx6poAq\"}", "29b6c68b692300ba884698b96819d480bd0d17f4d6740587dc72b917e23462a7"},
	{"resource-create", 200, "{\"resource\":{\"id\":\"296571fd-9b6f-4084-afa1-3ccfc7b2b191\",\"audience\":\"https://api.example.test/management\",\"allowed_client_ids\":[\"O7WimIKtyVxcispIMQ8HWpud\"],\"allowed_grant_types\":[\"authorization_code\",\"refresh_token\",\"client_credentials\",\"urn:ietf:params:oauth:grant-type:device_code\"],\"allowed_scopes\":[\"openid\",\"email\",\"profile\",\"management:read\",\"management:write\"],\"introspection_client_id\":\"4CXJ8Hobd6lNPWXeGNEntzTliAXZSXfD\",\"token_endpoint_auth_method\":\"client_secret_basic\",\"auth_epoch\":1,\"enabled\":true,\"created_at\":1790748356,\"updated_at\":1790748356},\"introspection_secret\":\"ymCNlaeMJQ6kBI7TeirFHYEMy9ba2fR3YhCg27zZ8eI6BPwi\"}", "d19080007397af17e49c3d1ce8d1b47c7702ab7b83996028de50ea629ea3ffb0"},
	{"resource-create", 200, "{\"resource\":{\"id\":\"fa3e592f-cc13-490a-9a62-6046f00e89fd\",\"audience\":\"https://api.example.test/management\",\"allowed_client_ids\":[\"RxrjGqh3AbqzSsmSOT8Xbwvy\"],\"allowed_grant_types\":[\"authorization_code\",\"refresh_token\",\"client_credentials\",\"urn:ietf:params:oauth:grant-type:device_code\"],\"allowed_scopes\":[\"openid\",\"email\",\"profile\",\"management:read\",\"management:write\"],\"introspection_client_id\":\"8pxXF8eyzZjDxZsgobBeCxx7RhWgYTwJ\",\"token_endpoint_auth_method\":\"client_secret_basic\",\"auth_epoch\":1,\"enabled\":true,\"created_at\":1790748356,\"updated_at\":1790748356},\"introspection_secret\":\"asKX4xHIMRx1Fk9zW30J8SK3ID35A3e4AnEnD7I4UNiulSOr\"}", "807f6500ce031175686c9b42f4cbdf2d57134b1719dd86a7b867848dcb8158fe"},
	{"resource-create", 200, "{\"resource\":{\"id\":\"78bf49eb-31c2-4ae1-9fef-b88751b2afc9\",\"audience\":\"https://api.example.test/management\",\"allowed_client_ids\":[\"JuDmfb6pJsPF8l97tTDUIEQr\"],\"allowed_grant_types\":[\"authorization_code\",\"refresh_token\",\"client_credentials\",\"urn:ietf:params:oauth:grant-type:device_code\"],\"allowed_scopes\":[\"openid\",\"email\",\"profile\",\"management:read\",\"management:write\"],\"introspection_client_id\":\"F9krNQUjEJGqZr2O2KNBWO2zfdaeNOxM\",\"token_endpoint_auth_method\":\"client_secret_basic\",\"auth_epoch\":1,\"enabled\":true,\"created_at\":1790748356,\"updated_at\":1790748356},\"introspection_secret\":\"Xs2Ja6V8XznoIu3xleKbPoux51EstXLAygVO07VVW9Spx9pY\"}", "0b18dc89efb3e02621f271a9701c9d5a25459abe4f5985dba9d800af30c65063"},
	{"resource-create", 200, "{\"resource\":{\"id\":\"ea97f36b-5333-4578-8c3c-1dd409f3096c\",\"audience\":\"https://api.example.test/documents\",\"allowed_client_ids\":[\"o7H0V1pqjKb9IO3ltjuznoEv\"],\"allowed_grant_types\":[\"authorization_code\",\"refresh_token\",\"client_credentials\",\"urn:ietf:params:oauth:grant-type:device_code\"],\"allowed_scopes\":[\"openid\",\"email\",\"profile\",\"management:read\",\"management:write\"],\"introspection_client_id\":\"AqtlkbOOGhoezA5wzzp9pNg1XOo0QA7e\",\"token_endpoint_auth_method\":\"client_secret_basic\",\"auth_epoch\":1,\"enabled\":true,\"created_at\":1790748356,\"updated_at\":1790748356},\"introspection_secret\":\"jWziOD93ri8rPovpqL3YDc2RVPYUfXMUTyVfewWAcjfIkbmJ\"}", "f74b262aabbdaaa2adc3a9282666ce5571caffbc4d85e4df82843ce80c17ecbe"},
	{"resource-delete", 204, "", "9bedb869285cfe4cd0ab8f7dd9d5f94b61e0cfa76744e8a3c61b2bc0123326c7"},
	{"resource-introspection", 200, "{\"active\":true,\"token_type\":\"Bearer\",\"iss\":\"http://localhost:43371/resources\",\"sub\":\"RxrjGqh3AbqzSsmSOT8Xbwvy\",\"aud\":\"https://api.example.test/management\",\"client_id\":\"RxrjGqh3AbqzSsmSOT8Xbwvy\",\"scope\":\"management:read\",\"exp\":1790751956,\"iat\":1790748356,\"sid\":null,\"principal_kind\":\"service\"}", "e55cc799fa6a845bca14ee615ac05c3ec8e9302b2f0668e47de70a27e56c143f"},
	{"resource-introspection", 200, "{\"active\":true,\"token_type\":\"Bearer\",\"iss\":\"http://localhost:36701/resources\",\"sub\":\"ab0357f4-db70-4ddd-94fa-95255ceb3317\",\"aud\":\"https://api.example.test/management\",\"client_id\":\"JuDmfb6pJsPF8l97tTDUIEQr\",\"scope\":\"openid management:read management:write\",\"exp\":1790751956,\"iat\":1790748356,\"sid\":\"4fea65ec-3bff-408d-9b0e-b8ec2ab97af6\",\"principal_kind\":\"user\"}", "63a1d84472e9b6cb0b436f2d6e0b2eac4889cfc1545b0bd41395556268b23219"},
	{"resource-introspection", 200, "{\"active\":false}", "e617e84fc11c7039529b177aae9c77e227c3209267fc6b6969064d9c70421a6a"},
	{"resource-introspection", 200, "{\"active\":false}", "e617e84fc11c7039529b177aae9c77e227c3209267fc6b6969064d9c70421a6a"},
	{"resource-introspection", 200, "{\"active\":false}", "e617e84fc11c7039529b177aae9c77e227c3209267fc6b6969064d9c70421a6a"},
	{"resource-introspection", 200, "{\"active\":false}", "e617e84fc11c7039529b177aae9c77e227c3209267fc6b6969064d9c70421a6a"},
	{"resource-introspection", 200, "{\"active\":false}", "e617e84fc11c7039529b177aae9c77e227c3209267fc6b6969064d9c70421a6a"},
	{"resource-introspection", 200, "{\"active\":false}", "e617e84fc11c7039529b177aae9c77e227c3209267fc6b6969064d9c70421a6a"},
	{"resource-introspection", 200, "{\"active\":false}", "e617e84fc11c7039529b177aae9c77e227c3209267fc6b6969064d9c70421a6a"},
	{"resource-introspection", 200, "{\"active\":false}", "e617e84fc11c7039529b177aae9c77e227c3209267fc6b6969064d9c70421a6a"},
	{"resource-introspection", 200, "{\"active\":false}", "e617e84fc11c7039529b177aae9c77e227c3209267fc6b6969064d9c70421a6a"},
	{"resource-introspection", 200, "{\"active\":false}", "bdb9355a88f00e094427a6d511148944fb40543539b30057db9632ca8a1aae86"},
	{"resource-introspection", 200, "{\"active\":true,\"token_type\":\"Bearer\",\"iss\":\"http://localhost:36701/resources\",\"sub\":\"ab0357f4-db70-4ddd-94fa-95255ceb3317\",\"aud\":\"https://api.example.test/management\",\"client_id\":\"JuDmfb6pJsPF8l97tTDUIEQr\",\"scope\":\"openid management:read\",\"exp\":1790751957,\"iat\":1790748357,\"sid\":\"4fea65ec-3bff-408d-9b0e-b8ec2ab97af6\",\"principal_kind\":\"user\"}", "eca0a4f75fd22b998477e9a5e8cb8658c08e3cba726d4733f459b0bc63c5018b"},
	{"resource-introspection", 200, "{\"active\":false}", "bdb9355a88f00e094427a6d511148944fb40543539b30057db9632ca8a1aae86"},
	{"resource-introspection", 200, "{\"active\":false}", "bdb9355a88f00e094427a6d511148944fb40543539b30057db9632ca8a1aae86"},
	{"resource-introspection", 200, "{\"active\":false}", "bdb9355a88f00e094427a6d511148944fb40543539b30057db9632ca8a1aae86"},
	{"resource-introspection", 200, "{\"active\":false}", "bdb9355a88f00e094427a6d511148944fb40543539b30057db9632ca8a1aae86"},
	{"resource-introspection", 200, "{\"active\":false}", "bdb9355a88f00e094427a6d511148944fb40543539b30057db9632ca8a1aae86"},
	{"resource-introspection", 200, "{\"active\":false}", "bdb9355a88f00e094427a6d511148944fb40543539b30057db9632ca8a1aae86"},
	{"resource-invalid-client", 401, "{\"error\":\"invalid_client\",\"error_description\":\"invalid resource authentication\"}", "63aba13dedbef85bed0d92b485f9f0a9d03790732b2c03cb9dcfcfb816dbe012"},
	{"resource-list", 200, "{\"resources\":[{\"id\":\"296571fd-9b6f-4084-afa1-3ccfc7b2b191\",\"audience\":\"https://api.example.test/management\",\"allowed_client_ids\":[\"O7WimIKtyVxcispIMQ8HWpud\"],\"allowed_grant_types\":[\"authorization_code\",\"refresh_token\",\"client_credentials\",\"urn:ietf:params:oauth:grant-type:device_code\"],\"allowed_scopes\":[\"openid\",\"email\",\"profile\",\"management:read\",\"management:write\"],\"introspection_client_id\":\"4CXJ8Hobd6lNPWXeGNEntzTliAXZSXfD\",\"token_endpoint_auth_method\":\"client_secret_basic\",\"auth_epoch\":4,\"enabled\":false,\"created_at\":1790748356,\"updated_at\":1790748356},{\"id\":\"652d3a14-bfef-4d2d-91e4-5926d1e613bb\",\"audience\":\"https://api.example.test/documents\",\"allowed_client_ids\":[\"O7WimIKtyVxcispIMQ8HWpud\"],\"allowed_grant_types\":[\"authorization_code\",\"refresh_token\",\"client_credentials\",\"urn:ietf:params:oauth:grant-type:device_code\"],\"allowed_scopes\":[\"openid\",\"email\",\"profile\",\"management:read\",\"management:write\"],\"introspection_client_id\":\"m5dk7618Y6pAIGwcynIa0bnRr6qrjl8E\",\"token_endpoint_auth_method\":\"client_secret_basic\",\"auth_epoch\":1,\"enabled\":true,\"created_at\":1790748356,\"updated_at\":1790748356}]}", "ec61391b91e0c7d5dbcb19eb983191d33c3ad1b9557a3bc50c84ee2a3f7def24"},
	{"resource-stale-credentials", 401, "{\"error\":\"invalid_client\",\"error_description\":\"invalid resource authentication\"}", "124ed85cfa40cf42ccaebea41442fff85d5df90d2778bd55fc38bddef8dec592"},
	{"resource-storage-error", 500, "{\"error\":\"server_error\",\"error_description\":\"client authentication is temporarily unavailable\"}", "86e55d51a0d71aa066fd78e8cd2435ebd3f16cde9533eb1a1e3fce34ef99b0b0"},
	{"resource-update", 200, "{\"resource\":{\"id\":\"296571fd-9b6f-4084-afa1-3ccfc7b2b191\",\"audience\":\"https://api.example.test/management\",\"allowed_client_ids\":[\"O7WimIKtyVxcispIMQ8HWpud\"],\"allowed_grant_types\":[\"authorization_code\",\"refresh_token\",\"client_credentials\",\"urn:ietf:params:oauth:grant-type:device_code\"],\"allowed_scopes\":[\"openid\",\"email\",\"profile\",\"management:read\",\"management:write\"],\"introspection_client_id\":\"4CXJ8Hobd6lNPWXeGNEntzTliAXZSXfD\",\"token_endpoint_auth_method\":\"client_secret_basic\",\"auth_epoch\":3,\"enabled\":true,\"created_at\":1790748356,\"updated_at\":1790748356},\"introspection_secret\":null}", "a0f5389bbea608b258411b42a48df81ab99d02e6460790957cf067b6226acb73"},
	{"resource-update-rotated", 200, "{\"resource\":{\"id\":\"296571fd-9b6f-4084-afa1-3ccfc7b2b191\",\"audience\":\"https://api.example.test/management\",\"allowed_client_ids\":[\"O7WimIKtyVxcispIMQ8HWpud\"],\"allowed_grant_types\":[\"authorization_code\",\"refresh_token\",\"client_credentials\",\"urn:ietf:params:oauth:grant-type:device_code\"],\"allowed_scopes\":[\"openid\",\"email\",\"profile\",\"management:read\",\"management:write\"],\"introspection_client_id\":\"4CXJ8Hobd6lNPWXeGNEntzTliAXZSXfD\",\"token_endpoint_auth_method\":\"client_secret_basic\",\"auth_epoch\":2,\"enabled\":true,\"created_at\":1790748356,\"updated_at\":1790748356},\"introspection_secret\":\"lGWX3rWRYyFDkgP5v18zXBZ4VODXucungfGtsi8dnxOgdsGe\"}", "d9a01b4ac0a57a0509ed0b1fac3dbc1389dfbe956dd71c0e31a9013bd23c16a5"},
}

func TestTSAuthResourceCapturedPolicyResponses(t *testing.T) {
	for _, response := range capturedResourceResponses {
		var err error
		switch response.name {
		case "resource-create", "resource-update", "resource-update-rotated":
			result := TSAuthOAuthResourceResult{requireSecret: response.name != "resource-update"}
			err = result.decodeHTTPResponse(response.status, []byte(response.body))
			if err == nil && result.Resource.AuthEpoch == 0 {
				t.Fatal("resource epoch lost")
			}
			if err == nil && result.IntrospectionSecret != nil && strings.Contains(fmt.Sprintf("%v %#v", result, result), *result.IntrospectionSecret) {
				t.Fatal("resource secret diagnostic leak")
			}
		case "resource-list":
			var result tsAuthResourcePolicyList
			err = result.decodeHTTPResponse(response.status, []byte(response.body))
		case "client-create", "client-update", "client-update-rotated":
			var view struct {
				Client struct {
					Public bool `json:"public"`
				} `json:"client"`
			}
			json.Unmarshal([]byte(response.body), &view)
			result := TSAuthOAuthPolicyClientResult{wrapper: response.name != "client-update", requireSecret: response.name == "client-update-rotated" || response.name == "client-create" && !view.Client.Public}
			err = result.decodeHTTPResponse(response.status, []byte(response.body))
			if err == nil && result.Client.AuthEpoch == 0 {
				t.Fatal("client epoch lost")
			}
			if err == nil && result.ClientSecret != nil && strings.Contains(fmt.Sprintf("%v %#v", result, result), *result.ClientSecret) {
				t.Fatal("client secret diagnostic leak")
			}
		case "client-list":
			var result tsAuthClientPolicyList
			err = result.decodeHTTPResponse(response.status, []byte(response.body))
		default:
			continue
		}
		if err != nil {
			t.Fatalf("capture %s/%s: %v", response.name, response.sourceSHA, err)
		}
	}
}

func TestTSAuthResourcePolicyEpochAndPatchBranch(t *testing.T) {
	var clientBare, clientRotated, resource string
	for _, response := range capturedResourceResponses {
		switch response.name {
		case "client-update":
			clientBare = response.body
		case "client-update-rotated":
			clientRotated = response.body
		case "resource-update":
			resource = response.body
		}
	}
	for _, test := range []struct {
		body            string
		wrapper, secret bool
	}{{clientBare, true, true}, {clientRotated, false, false}} {
		result := TSAuthOAuthPolicyClientResult{wrapper: test.wrapper, requireSecret: test.secret}
		if result.decodeHTTPResponse(200, []byte(test.body)) == nil {
			t.Fatal("wrong PATCH response branch admitted")
		}
	}
	var object map[string]json.RawMessage
	json.Unmarshal([]byte(resource), &object)
	var policy map[string]json.RawMessage
	json.Unmarshal(object["resource"], &policy)
	policy["auth_epoch"] = json.RawMessage("18446744073709551615")
	object["resource"], _ = json.Marshal(policy)
	body, _ := json.Marshal(object)
	var result TSAuthOAuthResourceResult
	if err := result.decodeHTTPResponse(200, body); err != nil || result.Resource.AuthEpoch != math.MaxUint64 {
		t.Fatalf("full u64 epoch: %v", err)
	}
	for _, bad := range []string{"18446744073709551616", "1.0", "1e3", "-1", "null", "0", "\"1\""} {
		policy["auth_epoch"] = json.RawMessage(bad)
		object["resource"], _ = json.Marshal(policy)
		body, _ = json.Marshal(object)
		if result.decodeHTTPResponse(200, body) == nil {
			t.Fatalf("invalid epoch %s", bad)
		}
	}
	empty := []string{}
	patch := UpdateTSAuthOAuthResourceRequest{ExpectedAuthEpoch: math.MaxUint64, AllowedScopes: &empty}
	body, _ = json.Marshal(patch)
	if string(body) != `{"expected_auth_epoch":18446744073709551615,"allowed_scopes":[]}` {
		t.Fatalf("epoch/empty scope wire %s", body)
	}
	var nilVector []string
	if tsAuthPolicyPatch(nil, nil, nil, &nilVector) == nil {
		t.Fatal("null patch vector admitted")
	}
	if tsAuthPolicy([]string{"https://api.example.test/a"}, nil, []string{"client_credentials"}, []string{"read"}, true) == nil {
		t.Fatal("public service grant admitted")
	}
}
