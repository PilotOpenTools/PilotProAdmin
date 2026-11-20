# frozen_string_literal: true

# Шаг установщика после восстановления или чистой установки GitLab (install.sh / install.ps1):
#   docker cp gitlab-post-restore.rb gitlab:/tmp/post.rb
#   docker exec -e ... gitlab gitlab-rails runner /tmp/post.rb
# Что делает (всё идемпотентно, значения секретов в вывод не попадают):
#   1. закрывает саморегистрацию (signup_enabled=false);
#   2. задаёт переменные групп модулей DEPLOY_KEY_TEST (masked), _STAGE и _PROD (masked, protected), удаляет старую DEPLOY_KEY;
#   3. в каждом проекте групп модулей и группы отчётов заменяет старый адрес GitLab в nuget.config и коммитит с [skip ci];
#   4. в проектах групп модулей защищает main и master (push: No one, merge: Maintainers), release/* и dev (push и merge: Maintainers);
#   5. в проектах групп модулей и отчётов, где есть CI (ci_config_path или .gitlab-ci.yml), включает «Pipelines must succeed»
#      (only_allow_merge_if_pipeline_succeeds); проекты без CI не трогает, иначе их merge request нечем будет влить;
#   6. для групп модулей и отчётов задаёт защиту ветки по умолчанию у новых проектов (default_branch_protection_defaults, GitLab 17.0+):
#      push и merge - Maintainers, первый push может сделать Developer. «No one» в этом поле GitLab не принимает (только 30 и 40);
#      если настройка недоступна (например, запрещена на уровне экземпляра), это предупреждение, а не ошибка;
#   7. предупреждает о модулях со старым шаблоном .gitlab-ci.yml (переменные ключа с префиксом перед DEPLOY_KEY или нет X-Job-Token):
#      их выкладка не пройдёт до «Обновить CI»;
#   8. добавляет адрес возврата админки в Redirect URI её OAuth-приложения (прежние адреса остаются);
#      при чистой установке (передан OAUTH_CLIENT_SECRET, приложения нет) создаёт это приложение;
#   9. если проекта общих проверок (CHECKS_PROJECT, по умолчанию pilot-ci/checks) нет - создаёт его публичным
#      (с группой) и кладёт в main файлы из CHECKS_DIR; существующий проект не трогает. Без него pipeline
#      автоматизаций и отчётов (ci_config_path = automation.yml@pilot-ci/checks) падает с config_error;
#  10. если передан RUNNER_TOKEN_FILE (у runner'а нет config.toml) - создаёт runner экземпляра и пишет его токен
#      в этот файл внутри контейнера (права 600); регистрирует runner установщик.
# Окружение: DEPLOY_KEY_TEST/_STAGE/_PROD (секреты), GROUP_URLS (адреса групп модулей через запятую),
#   REPORTS_GROUP_URL, OLD_HOST (без него nuget.config не трогается), NEW_URL (http://хост:порт),
#   ALLOW_MISSING_GROUPS=1 (чистая установка: нет группы - пропуск и код выхода 2 вместо ошибки),
#   OAUTH_CLIENT_ID (Application ID приложения админки) и ADMIN_CALLBACK (http://адрес-админки/GitLab/Callback),
#   CHECKS_DIR (папка с файлами ci-checks внутри контейнера; пусто - шаг 9 пропускается), CHECKS_PROJECT,
#   RUNNER_TOKEN_FILE (пусто - шаг 10 пропускается).
# Вывод только латиницей: консоль Windows искажает UTF-8, который docker exec отдаёт установщику.
# Самопроверка чистых функций без GitLab:
#   docker run --rm -e SELFTEST=1 -v "<stack>/Install:/s:ro" ruby:3.3-alpine ruby /s/gitlab-post-restore.rb

require 'net/http'
require 'json'
require 'uri'
require 'erb'
require 'base64'
require 'securerandom'

PAT_NAME = 'post-restore'
VARIABLES = { 'DEPLOY_KEY_TEST' => false, 'DEPLOY_KEY_STAGE' => true, 'DEPLOY_KEY_PROD' => true }.freeze # имя => protected
OLD_VARIABLE = 'DEPLOY_KEY'
# Правила защиты: имя ветки или шаблон => [push, merge]; 0 - No one, 40 - Maintainers.
# dev - Maintainers: при подключении модуля админка коммитит в dev .gitlab-ci.yml от имени выпускающего.
PROTECTION = { 'main' => [0, 40], 'master' => [0, 40], 'release/*' => [40, 40], 'dev' => [40, 40] }.freeze
CI_FILE = '.gitlab-ci.yml'
# Защита ветки по умолчанию для новых проектов группы: GitLab принимает здесь только 30 (Developer) и 40 (Maintainer).
BRANCH_DEFAULTS = {
  allowed_to_push: [{ access_level: 40 }], allowed_to_merge: [{ access_level: 40 }],
  allow_force_push: false, developer_can_initial_push: true # админка создаёт проект и пушит в него первый коммит от имени пользователя
}.freeze

# ---------------------------------------------------------------- чистые функции

# Путь группы из адреса в настройках админки: без /-/…, суффикса .git и крайних «/» (как GitLabLocation.TryParse).
def group_path(url)
  path = URI.parse(url.to_s.strip).path.to_s.sub(%r{/-/.*\z}, '').sub(/\.git\z/, '').gsub(%r{\A/+|/+\z}, '')
  path.empty? ? nil : path
rescue URI::InvalidURIError
  nil
end

# «хост» или «хост:порт» из адреса; порт схемы по умолчанию опускается.
def authority(url)
  u = URI.parse(url.to_s.strip)
  return nil if u.host.to_s.empty?

  u.port == u.default_port ? u.host : "#{u.host}:#{u.port}"
rescue URI::InvalidURIError
  nil
end

def nuget_file?(path)
  File.basename(path).downcase == 'nuget.config'
end

def nuget_branch?(name)
  %w[dev main master].include?(name) || name.start_with?('release/', 'hotfix/')
end

# У GitLab «*» в имени защищённой ветки совпадает и с «/».
def covers?(pattern, branch)
  File.fnmatch(pattern, branch)
end

def levels(rule, key)
  rule[key].map { |l| l['access_level'] }.sort
end

# Путь проекта проверок из CHECKS_PROJECT или из ci_config_path вида «automation.yml@pilot-ci/checks».
def checks_project_path(value)
  path = value.to_s.strip.split('@', 2).last.to_s.sub(/\.git\z/, '').gsub(%r{\A/+|/+\z}, '')
  path.include?('/') ? path : nil
end

# Файлы проверок для коммита: только обычные файлы верхнего уровня, тесты и кэш Python не нужны; переводы строк - LF.
def checks_files(dir)
  Dir.children(dir).sort.filter_map do |name|
    full = File.join(dir, name)
    next unless File.file?(full)
    next if name.start_with?('.') || name.start_with?('test_') || name.end_with?('.pyc')

    [name, File.binread(full).gsub("\r\n", "\n")]
  end
end

# Нужна ли проекту «Pipelines must succeed»: без CI merge request не получит pipeline и не вливался бы вовсе.
def merge_gate_needed?(project_json, ci_file_present)
  return false if project_json['only_allow_merge_if_pipeline_succeeds'] == true

  !project_json['ci_config_path'].to_s.strip.empty? || ci_file_present
end

# Уже ли у группы нужная защита ветки по умолчанию (ответ GitLab: массивы объектов с access_level).
def branch_defaults_set?(group_json)
  cur = group_json['default_branch_protection_defaults']
  return false unless cur.is_a?(Hash)

  lv = ->(h, k) { Array(h[k]).map { |l| l['access_level'] || l[:access_level] }.sort }
  BRANCH_DEFAULTS.slice(:allowed_to_push, :allowed_to_merge).all? { |k, _| lv.call(cur, k.to_s) == lv.call(BRANCH_DEFAULTS, k) } &&
    cur['allow_force_push'] == BRANCH_DEFAULTS[:allow_force_push] &&
    cur['developer_can_initial_push'] == BRANCH_DEFAULTS[:developer_can_initial_push]
end

# Redirect URI приложения Doorkeeper: адреса через перевод строки; новый добавляется, прежние остаются.
def merge_redirect(current, callback)
  uris = current.to_s.split(/\s+/).reject(&:empty?)
  uris.include?(callback) ? uris.join("\n") : (uris + [callback]).join("\n")
end

# Переменная ключа выкладки с префиксом перед DEPLOY_KEY: так назывались переменные групп до переименования, сейчас
# их там нет. MODULES_ - не то: MODULES_DEPLOY_KEY_* встречается в комментариях нового шаблона (ключи в .env админки).
LEGACY_DEPLOY_KEY = /(?<![A-Z0-9_])(?!MODULES_)[A-Z][A-Z0-9]*_DEPLOY_KEY/n

# Шаблон CI, с которым выкладка не пройдёт: переменные ключа со старым префиксом (в группах их больше нет)
# или обращение к админке без X-Job-Token (админка такую выкладку отвергает).
def old_ci?(content)
  text = content.to_s.b
  text.match?(LEGACY_DEPLOY_KEY) || (text.include?('/api/ci/') && !text.include?('X-Job-Token'))
end

# Заменяет только //СТАРЫЙ_ХОСТ(:порт)?/ в значениях с /api/v4/: чужие источники и другие адреса остаются, а байты (BOM, CRLF, кодировка) не меняются.
def rewrite_nuget(content, old_host, new_authority)
  from = /\A((?:[a-z][a-z0-9+.\-]*:)?\/\/)#{Regexp.escape(old_host)}(?::\d+)?\//i
  content.b.gsub(/[^\s"'<>]+/n) do |token|
    token.include?('/api/v4/') ? token.sub(from) { "#{Regexp.last_match(1)}#{new_authority}/" } : token
  end
end

# ---------------------------------------------------------------- самопроверка (без GitLab)

if ENV['SELFTEST'] == '1'
  fails = 0
  check = lambda do |cond, msg|
    puts "  #{cond ? 'ok' : 'NOT OK'}: #{msg}"
    fails += 1 unless cond
  end
  url = ->(host) { "http://#{host}/api/v4/projects/9/packages/nuget/index.json" }
  line = ->(host) { %(<add key="cicd" value="#{url.call(host)}" />) }

  check.call(rewrite_nuget(line.call('10.0.0.9:8080'), '10.0.0.9', '10.0.0.5:8080') == line.call('10.0.0.5:8080'), 'old host with port -> new host with port')
  check.call(rewrite_nuget(line.call('10.0.0.9'), '10.0.0.9', '10.0.0.5:8080') == line.call('10.0.0.5:8080'), 'old host without port -> new host with port')
  check.call(rewrite_nuget(line.call('10.0.0.9:8080'), '10.0.0.9', '10.0.0.5') == line.call('10.0.0.5'), 'new address without port')
  check.call(rewrite_nuget(line.call('GitLab.Example.Local:8080'), 'gitlab.example.local', 'h:1') == line.call('h:1'), 'host name is case-insensitive')
  check.call(rewrite_nuget(%(<add key='a' value='#{url.call('10.0.0.9:8080')}'/>), '10.0.0.9', 'h') == %(<add key='a' value='#{url.call('h')}'/>), 'single quotes')

  nuget_org = '<add key="nuget.org" value="https://api.nuget.org/v3/index.json" />'
  other = line.call('10.0.0.7:8080')
  no_api = '<add key="x" value="http://10.0.0.9:8080/other/index.json" />'
  longer = line.call('110.0.0.9:8080')
  suffix = line.call('10.0.0.9.evil.example:8080')
  [nuget_org, other, no_api, longer, suffix].each_with_index do |s, i|
    check.call(rewrite_nuget(s, '10.0.0.9', 'h:1') == s.b, "untouched: #{%w[nuget.org other-host no-api/v4 host-110.0.0.9 host-suffix][i]}")
  end

  bom = "\xEF\xBB\xBF".b
  file = bom + "<?xml version=\"1.0\"?>\r\n<configuration>\r\n  #{nuget_org}\r\n  #{line.call('10.0.0.9:8080')}\r\n  <!-- \xE9 -->\r\n</configuration>\r\n".b
  want = file.sub('10.0.0.9:8080', '10.0.0.5:8080')
  got = rewrite_nuget(file, '10.0.0.9', '10.0.0.5:8080')
  check.call(got == want, 'BOM, CRLF and non-UTF-8 bytes preserved, only the address differs')
  check.call(rewrite_nuget(got, '10.0.0.9', '10.0.0.5:8080') == got, 'second pass changes nothing')

  check.call(group_path('http://10.0.0.5:8080/client-modules') == 'client-modules', 'group_path: plain')
  check.call(group_path('http://h/g/sub.git') == 'g/sub', 'group_path: .git suffix')
  check.call(group_path('http://h/g/sub/-/tree/main/') == 'g/sub', 'group_path: /-/ tail')
  check.call(group_path('http://h/') .nil? && group_path('').nil?, 'group_path: no path')
  check.call(authority('http://10.0.0.5:8080') == '10.0.0.5:8080' && authority('http://10.0.0.5') == '10.0.0.5' && authority('') .nil?, 'authority: port, default port, empty')

  check.call(%w[dev main master release/1.2.0 hotfix/x].all? { |b| nuget_branch?(b) } && !%w[feature/x develop v1.0.0 release].any? { |b| nuget_branch?(b) }, 'nuget_branch?')
  check.call(nuget_file?('NuGet.Config') && nuget_file?('a/b/nuget.config') && !nuget_file?('nuget.config.bak'), 'nuget_file?: any depth and case')
  check.call(covers?('release/*', 'release/1.2.0') && covers?('main', 'main') && !covers?('main', 'main2') && !covers?('release/*', 'dev'), 'covers?')
  check.call(levels({ 'push_access_levels' => [{ 'access_level' => 40 }, { 'access_level' => 0 }] }, 'push_access_levels') == [0, 40], 'levels')
  # Фрагменты шаблонов: новый (как ci-template в main) и прежний - тот же текст, но переменные групп с префиксом проекта.
  current_ci = <<~YML
    #   DEPLOY_KEY_TEST  - ключ контура тест  (= MODULES_DEPLOY_KEY_TEST админки), Masked;
    #   DEPLOY_KEY_PROD  - ключ контура прод (= MODULES_DEPLOY_KEY_PROD), Masked и Protected;
        test)  KEY_NAME=DEPLOY_KEY_TEST;  DEPLOY_KEY="${DEPLOY_KEY_TEST:-}" ;;
        prod)  KEY_NAME=DEPLOY_KEY_PROD;  DEPLOY_KEY="${DEPLOY_KEY_PROD:-}" ;;
      curl -H "X-Job-Token: $CI_JOB_TOKEN" -H "X-Deploy-Key: $DEPLOY_KEY" "$ADMIN_URL/api/ci/modules/deploy"
  YML
  legacy_ci = current_ci.gsub(/(?<![A-Z_])DEPLOY_KEY_(TEST|PROD)/, 'PRJ_DEPLOY_KEY_\1')
  check.call(legacy_ci != current_ci && old_ci?(legacy_ci) && !old_ci?(current_ci), 'old_ci?: legacy template (prefixed group variables) vs current one')
  check.call(old_ci?('-H "X-Deploy-Key: $PRJ_DEPLOY_KEY"') && old_ci?('-H "X-Deploy-Key: $DEPLOY_KEY" "$ADMIN_URL/api/ci/modules/deploy"') &&
             !old_ci?('MODULES_DEPLOY_KEY_TEST') && !old_ci?('') && !old_ci?(nil), 'old_ci?: shared key, no X-Job-Token, MODULES_ in comments, empty')
  cb = 'http://10.0.0.5:5300/GitLab/Callback'
  check.call(merge_redirect('http://localhost:5300/GitLab/Callback', cb) == "http://localhost:5300/GitLab/Callback\n#{cb}" &&
             merge_redirect("a\r\n#{cb}", cb) == "a\n#{cb}" && merge_redirect(nil, cb) == cb, 'merge_redirect')

  check.call(checks_project_path('automation.yml@pilot-ci/checks') == 'pilot-ci/checks' && checks_project_path('pilot-ci/checks') == 'pilot-ci/checks' &&
             checks_project_path('/g/sub/checks.git') == 'g/sub/checks' && checks_project_path('checks').nil? && checks_project_path('').nil?, 'checks_project_path')
  check.call(merge_gate_needed?({ 'ci_config_path' => '' }, true) && merge_gate_needed?({ 'ci_config_path' => 'ci/x.yml@g/central' }, false), 'merge_gate_needed?: CI file or custom ci_config_path')
  check.call(!merge_gate_needed?({ 'ci_config_path' => nil }, false) && !merge_gate_needed?({ 'only_allow_merge_if_pipeline_succeeds' => true, 'ci_config_path' => 'x' }, true), 'merge_gate_needed?: no CI, or already set')
  good = { 'default_branch_protection_defaults' => { 'allowed_to_push' => [{ 'access_level' => 40 }], 'allowed_to_merge' => [{ 'access_level' => 40 }], 'allow_force_push' => false, 'developer_can_initial_push' => true } }
  check.call(branch_defaults_set?(good) && !branch_defaults_set?({}) && !branch_defaults_set?({ 'default_branch_protection_defaults' => good['default_branch_protection_defaults'].merge('allowed_to_push' => [{ 'access_level' => 30 }]) }), 'branch_defaults_set?')

  puts fails.zero? ? 'ALL OK' : "FAILED: #{fails}"
  exit(fails.zero? ? 0 : 1)
end

# ---------------------------------------------------------------- работа с GitLab

$errors = 0
$missing = 0
$old_ci = []
$secrets = []

def say(msg)
  text = msg.to_s
  $secrets.each { |v| text = text.gsub(v, '***') }
  puts "[post] #{text}"
  $stdout.flush
end

def err(msg)
  $errors += 1
  say("ERROR: #{msg}")
end

def env(name)
  ENV[name].to_s.dup.force_encoding('UTF-8').strip
end

def esc(str)
  ERB::Util.url_encode(str)
end

# Каждый шаг отдельно: сбой одного не отменяет остальные.
def guard(what)
  yield
rescue Api::Error, JSON::ParserError, SystemCallError, IOError, Timeout::Error => e
  err("#{what}: #{e.message}")
end

class Api
  class Error < StandardError
    attr_reader :code

    def initialize(msg, code = nil)
      super(msg)
      @code = code
    end
  end

  def initialize(token)
    @token = token
  end

  def request(verb, path, body = nil)
    uri = URI("http://127.0.0.1/api/v4#{path}")
    req = Net::HTTP.const_get(verb.to_s.capitalize).new(uri)
    req['PRIVATE-TOKEN'] = @token
    if body
      req['Content-Type'] = 'application/json'
      req.body = JSON.generate(body)
    end
    Net::HTTP.start(uri.host, uri.port, read_timeout: 300) { |http| http.request(req) }
  end

  def call(verb, path, body = nil, ok: [200, 201, 204])
    res = request(verb, path, body)
    raise Error.new("#{verb.to_s.upcase} #{path.split('?').first} -> #{res.code} #{res.body.to_s[0, 200]}", res.code.to_i) unless ok.include?(res.code.to_i)

    res
  end

  # Все страницы списка.
  def list(path)
    page = 1
    all = []
    loop do
      res = call(:get, "#{path}#{path.include?('?') ? '&' : '?'}per_page=100&page=#{page}")
      all.concat(JSON.parse(res.body))
      nxt = res['x-next-page'].to_s
      return all if nxt.empty?

      page = nxt.to_i
    end
  end
end

# Временный токен root со сроком в сутки; отзывается в ensure основного блока.
def create_pat(user)
  user.personal_access_tokens.where(name: PAT_NAME, revoked: false).each(&:revoke!) # остаток оборванного запуска
  attrs = { name: PAT_NAME, scopes: %i[api create_runner], expires_at: 1.day.from_now.to_date }
  org = defined?(Organizations::Organization) ? Organizations::Organization.first : nil
  attrs[:organization] = org if org && PersonalAccessToken.reflect_on_association(:organization) # в новых версиях токену нужна организация
  token = "glpat-#{SecureRandom.alphanumeric(20)}"
  pat = user.personal_access_tokens.build(attrs)
  pat.set_token(token)
  pat.save!
  [pat, token]
end

# Без адреса возврата админки в приложении GitLab отвечает на вход «The redirect URI included is not valid».
# Чистая установка: приложения ещё нет, установщик передал Application ID и секрет из .env - создаём его
# (trusted: без экрана согласия, scope api - как App:GitLab:Scopes админки). ADMIN_CALLBACK - адреса через пробел.
def step_oauth(client_id, callback, secret = '')
  callbacks = callback.split(/\s+/).reject(&:empty?)
  return say('oauth: no client id or admin address, skipped') if client_id.empty? || callbacks.empty?

  app = Doorkeeper::Application.find_by(uid: client_id)
  unless app
    return say("WARNING: oauth application #{client_id} not found, set its Redirect URI to #{callbacks.join(' ')} by hand") if secret.empty?

    attrs = { name: 'PilotProAdmin', uid: client_id, secret: secret, redirect_uri: callbacks.join("\n"),
              scopes: 'api', confidential: true, trusted: true }
    # GitLab 18+: приложение экземпляра принадлежит организации (check organization_id IS NOT NULL).
    attrs[:organization_id] = Organizations::Organization.order(:id).pick(:id) if Doorkeeper::Application.column_names.include?('organization_id')
    Doorkeeper::Application.create!(**attrs)
    return say("oauth: application PilotProAdmin created, redirect URI #{callbacks.join(' ')}")
  end

  uris = callbacks.reduce(app.redirect_uri.to_s) { |cur, cb| merge_redirect(cur, cb) }
  return say("oauth: redirect URI #{callbacks.join(' ')} already set") if uris == app.redirect_uri.to_s

  app.redirect_uri = uris
  app.save!
  say("oauth: redirect URI #{callbacks.join(' ')} added to #{app.name}")
rescue ActiveRecord::ActiveRecordError => e
  err("oauth: #{e.message}")
end

# Проект общих проверок: создаётся один раз; существующий (возможно, с правками заказчика) не трогается.
def step_checks(api, project_path, dir)
  return say('checks: no CHECKS_DIR, skipped') if dir.empty?
  return err("checks: folder #{dir} not found in the container") unless File.directory?(dir)
  return err('checks: cannot read the project path from CHECKS_PROJECT') unless project_path

  pid = esc(project_path)
  return say("checks: #{project_path} already exists, not touched") if api.call(:get, "/projects/#{pid}", nil, ok: [200, 404]).code == '200'

  files = checks_files(dir)
  return err("checks: no files in #{dir}") if files.empty?

  group_path, name = project_path.rpartition('/').values_at(0, 2)
  group = ensure_group(api, group_path)
  project = JSON.parse(api.call(:post, '/projects', { name: name, path: name, namespace_id: group['id'], visibility: 'public',
                                                      default_branch: 'main', initialize_with_readme: false,
                                                      description: 'Общие проверки CI автоматизаций и отчётов Pilot' }).body)
  actions = files.map { |n, body| { action: 'create', file_path: n, content: Base64.strict_encode64(body), encoding: 'base64' } }
  api.call(:post, "/projects/#{project['id']}/repository/commits", { branch: 'main', commit_message: 'Общие проверки из комплекта установки', actions: actions })
  say("checks: #{project_path} created (public), #{files.size} file(s) in main")
end

# Группа (и родительские) для проекта проверок; новые - публичные, иначе публичный проект в них не создать.
def ensure_group(api, path)
  res = api.call(:get, "/groups/#{esc(path)}", nil, ok: [200, 404])
  return JSON.parse(res.body) if res.code == '200'

  parent_path, name = path.rpartition('/').values_at(0, 2)
  body = { name: name, path: name, visibility: 'public' }
  body[:parent_id] = ensure_group(api, parent_path)['id'] unless parent_path.empty?
  group = JSON.parse(api.call(:post, '/groups', body).body)
  say("checks: group #{path} created (public)")
  group
end

# Runner экземпляра для установщика: токен - в файл внутри контейнера, не в вывод.
def step_runner(api, token_file)
  return if token_file.empty?

  res = JSON.parse(api.call(:post, '/user/runners', { runner_type: 'instance_type', description: 'local-docker-runner', run_untagged: true }).body)
  File.open(token_file, File::WRONLY | File::CREAT | File::TRUNC, 0o600) { |f| f.write(res['token']) }
  File.chmod(0o600, token_file)
  say("runner: instance runner #{res['id']} created, token written for the installer")
end

def step_signup(api)
  api.call(:put, '/application/settings', { signup_enabled: false })
  now = JSON.parse(api.call(:get, '/application/settings').body)['signup_enabled']
  now == false ? say('signup: disabled') : err("signup_enabled is #{now.inspect} after the update")
end

# Группы чистой установки ещё могут не существовать (ALLOW_MISSING_GROUPS=1): это пропуск (код выхода 2), а не сбой.
def existing_groups(api, urls, allow_missing)
  urls.select do |u|
    gp = group_path(u)
    unless gp
      err("cannot read the group path from #{u}")
      next false
    end

    next true if api.call(:get, "/groups/#{esc(gp)}", nil, ok: [200, 404]).code == '200'

    if allow_missing
      $missing += 1
      say("group #{gp}: not found yet, skipped")
    else
      err("group #{gp}: not found")
    end
    false
  rescue Api::Error => e
    err("group #{gp}: #{e.message}")
    false
  end
end

def group_projects(api, url)
  path = group_path(url)
  unless path
    err("cannot read the group path from #{url}")
    return []
  end

  api.list("/groups/#{esc(path)}/projects?include_subgroups=true&with_shared=false")
rescue Api::Error => e
  err("group #{path}: #{e.message}")
  []
end

def step_variables(api, group_paths, keys)
  group_paths.each do |gp|
    gid = esc(gp)
    VARIABLES.each do |name, protected_flag|
      value = keys[name]
      next say("variable #{name}: value is empty, skipped (#{gp})") if value.empty?

      guard("variable #{name} in #{gp}") do
        body = { value: value, masked: true, protected: protected_flag, raw: true }
        exists = api.call(:get, "/groups/#{gid}/variables/#{name}", nil, ok: [200, 404]).code == '200'
        exists ? api.call(:put, "/groups/#{gid}/variables/#{name}", body) : api.call(:post, "/groups/#{gid}/variables", body.merge(key: name))
        say("variable #{name}: set in #{gp} (masked#{protected_flag ? ', protected' : ''})")
      rescue Api::Error => e
        # 400 у masked - значение не подходит под маскирование (не меньше 8 символов, без пробелов и спецсимволов).
        raise Api::Error.new("#{e.message}#{' (check that the value fits the Masked format)' if e.code == 400}", e.code)
      end
    end
    guard("variable #{OLD_VARIABLE} in #{gp}") do
      removed = api.call(:delete, "/groups/#{gid}/variables/#{OLD_VARIABLE}", nil, ok: [204, 404]).code == '204'
      say("variable #{OLD_VARIABLE}: removed from #{gp}") if removed
    end
  end
end

# Проектная переменная с тем же именем перекрывает групповую.
def warn_overrides(api, project)
  names = api.list("/projects/#{project['id']}/variables").map { |v| v['key'] }
  (names & VARIABLES.keys).each { |n| say("WARNING: #{project['path_with_namespace']} overrides group variable #{n} at project level") }
end

def restore_rule(api, pid, rule)
  api.call(:post, "/projects/#{pid}/protected_branches",
           { name: rule['name'], push_access_level: levels(rule, 'push_access_levels').first,
             merge_access_level: levels(rule, 'merge_access_levels').first, allow_force_push: rule['allow_force_push'] })
end

# Правка nuget.config одной ветки. «No one» не пускает и администратора: такое правило на время коммита снимается и сразу возвращается.
def fix_nuget(api, project, branch, rules, old_host, new_authority)
  pid = project['id']
  tree = api.list("/projects/#{pid}/repository/tree?recursive=true&ref=#{esc(branch)}")
  actions = tree.select { |e| e['type'] == 'blob' && nuget_file?(e['path']) }.filter_map do |e|
    raw = api.call(:get, "/projects/#{pid}/repository/files/#{esc(e['path'])}/raw?ref=#{esc(branch)}").body.b
    changed = rewrite_nuget(raw, old_host, new_authority)
    { action: 'update', file_path: e['path'], content: Base64.strict_encode64(changed), encoding: 'base64' } unless changed == raw
  end
  return if actions.empty?

  blocked = rules.select { |r| covers?(r['name'], branch) && levels(r, 'push_access_levels').include?(0) }
  blocked.each { |r| api.call(:delete, "/projects/#{pid}/protected_branches/#{esc(r['name'])}") }
  begin
    api.call(:post, "/projects/#{pid}/repository/commits",
             { branch: branch, commit_message: "nuget.config: адрес GitLab #{old_host} -> #{new_authority} [skip ci]", actions: actions })
    say("nuget.config: #{project['path_with_namespace']}@#{branch}: #{actions.size} file(s) updated")
  ensure
    blocked.each do |r|
      restore_rule(api, pid, r)
    rescue Api::Error => e
      err("protection #{r['name']} of #{project['path_with_namespace']} was NOT restored: #{e.message}")
    end
  end
end

def protect_branches(api, project, branches)
  pid = project['id']
  PROTECTION.each do |name, (push, merge)|
    next if !name.include?('*') && !branches.include?(name) # main и master - только если ветка есть

    res = api.call(:get, "/projects/#{pid}/protected_branches/#{esc(name)}", nil, ok: [200, 404])
    if res.code == '200'
      rule = JSON.parse(res.body)
      next say("protection: #{project['path_with_namespace']} #{name}: already set") if levels(rule, 'push_access_levels') == [push] && levels(rule, 'merge_access_levels') == [merge]

      api.call(:delete, "/projects/#{pid}/protected_branches/#{esc(name)}")
    end
    api.call(:post, "/projects/#{pid}/protected_branches", { name: name, push_access_level: push, merge_access_level: merge, allow_force_push: false })
    say("protection: #{project['path_with_namespace']} #{name}: push=#{push} merge=#{merge}")
  end
end

# «Pipelines must succeed»: только проекты с CI; имя поля и ci_config_path - по GET /projects/:id.
def step_merge_gate(api, project)
  pid = project['id']
  info = JSON.parse(api.call(:get, "/projects/#{pid}").body)
  return say("merge gate: #{project['path_with_namespace']}: already set") if info['only_allow_merge_if_pipeline_succeeds'] == true

  branch = info['default_branch'].to_s
  ci_file = !branch.empty? && api.call(:get, "/projects/#{pid}/repository/files/#{esc('.gitlab-ci.yml')}?ref=#{esc(branch)}", nil, ok: [200, 404]).code == '200'
  return say("merge gate: #{project['path_with_namespace']}: no CI, skipped") unless merge_gate_needed?(info, ci_file)

  api.call(:put, "/projects/#{pid}", { only_allow_merge_if_pipeline_succeeds: true })
  say("merge gate: #{project['path_with_namespace']}: pipelines must succeed")
end

# Защита ветки по умолчанию для новых проектов группы. Отказ GitLab (политика экземпляра, старая версия) - предупреждение: на существующие проекты не влияет.
def step_group_defaults(api, group_path)
  gid = esc(group_path)
  return say("group defaults: #{group_path}: already set") if branch_defaults_set?(JSON.parse(api.call(:get, "/groups/#{gid}").body))

  api.call(:put, "/groups/#{gid}", { default_branch_protection_defaults: BRANCH_DEFAULTS })
  say("group defaults: #{group_path}: new projects get push=Maintainers merge=Maintainers on the default branch")
rescue Api::Error => e
  say("WARNING: group defaults: #{group_path}: not set (#{e.message})")
end

# (c) до (b): на момент восстановления ветки защищены автором, правки идут через снятие защиты.
def process_project(api, project, module_project, old_host, new_authority)
  name = project['path_with_namespace']
  return say("#{name}: archived, skipped") if project['archived']

  pid = project['id']
  branches = []
  rules = []
  guard("#{name}: branches") do
    branches = api.list("/projects/#{pid}/repository/branches").map { |b| b['name'] }
    rules = api.list("/projects/#{pid}/protected_branches")
  end
  if old_host
    branches.select { |b| nuget_branch?(b) }.each { |b| guard("#{name}@#{b}: nuget.config") { fix_nuget(api, project, b, rules, old_host, new_authority) } }
  end
  guard("#{name}: merge gate") { step_merge_gate(api, project) }
  return unless module_project

  guard("#{name}: branch protection") { protect_branches(api, project, branches) }
  guard("#{name}: project variables") { warn_overrides(api, project) }
  guard("#{name}: CI template") { check_ci(api, project, branches) }
end

# Шаблон CI смотрится в dev (туда его кладёт «Обновить CI»), без dev - в main или master.
def check_ci(api, project, branches)
  branch = %w[dev main master].find { |b| branches.include?(b) } or return
  res = api.call(:get, "/projects/#{project['id']}/repository/files/#{esc(CI_FILE)}/raw?ref=#{esc(branch)}", nil, ok: [200, 404])
  $old_ci << "#{project['path_with_namespace']}@#{branch}" if res.code == '200' && old_ci?(res.body)
end

def main
  keys = VARIABLES.keys.to_h { |k| [k, env(k)] }
  $secrets.concat(keys.values.select { |v| v.length >= 4 })
  modules = env('GROUP_URLS').split(',').map(&:strip).reject(&:empty?).uniq
  reports = env('REPORTS_GROUP_URL')
  old_host = env('OLD_HOST')
  new_authority = authority(env('NEW_URL'))
  old_host = '' if old_host == 'localhost'
  err('NEW_URL is not a valid address, nuget.config is skipped') if !old_host.empty? && new_authority.nil?
  fix = !old_host.empty? && new_authority

  user = User.find_by_username('root') || abort('[post] ERROR: user root not found')
  pat, token = create_pat(user)
  $secrets << token
  api = Api.new(token)
  begin
    guard('signup') { step_signup(api) }
    step_oauth(env('OAUTH_CLIENT_ID'), env('ADMIN_CALLBACK'), env('OAUTH_CLIENT_SECRET'))
    guard('checks') { step_checks(api, checks_project_path(env('CHECKS_PROJECT').empty? ? 'pilot-ci/checks' : env('CHECKS_PROJECT')), env('CHECKS_DIR')) }
    guard('runner') { step_runner(api, env('RUNNER_TOKEN_FILE')) }
    say('variables, protection and nuget.config: no module groups given, skipped') if modules.empty? && reports.empty?
    allow_missing = env('ALLOW_MISSING_GROUPS') == '1'
    modules = existing_groups(api, modules, allow_missing)
    reports = existing_groups(api, reports.empty? ? [] : [reports], allow_missing).first.to_s
    projects = (modules.flat_map { |u| group_projects(api, u).map { |p| [p, true] } } +
                (reports.empty? ? [] : group_projects(api, reports).map { |p| [p, false] })).uniq { |p, _| p['id'] }
    step_variables(api, modules.filter_map { |u| group_path(u) }.uniq, keys) unless modules.empty?
    (modules.filter_map { |u| group_path(u) } + (reports.empty? ? [] : [group_path(reports)].compact)).uniq.each { |gp| guard("group defaults #{gp}") { step_group_defaults(api, gp) } }
    projects.each { |p, module_project| process_project(api, p, module_project, fix ? old_host : nil, new_authority) }
  ensure
    begin
      pat.revoke!
    rescue StandardError => e
      err("temporary token was NOT revoked: #{e.message}")
    end
  end
  unless $old_ci.empty?
    say("WARNING: #{$old_ci.size} module(s) still use an old CI template (prefixed *_DEPLOY_KEY variables or no X-Job-Token), their deploy jobs will fail: #{$old_ci.join(', ')}")
    say("WARNING: open the admin, Modules page, and press the button that updates CI for all modules, then merge the MRs into dev")
  end
  say("done, errors: #{$errors}")
  exit($errors.positive? ? 1 : ($missing.positive? ? 2 : 0))
end

main
