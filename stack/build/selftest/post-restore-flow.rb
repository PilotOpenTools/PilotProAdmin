# frozen_string_literal: true

# Поток gitlab-post-restore.rb на подмене REST API GitLab (без GitLab): снятие и возврат защиты, коммит nuget.config, переменные, токен.
#   docker run --rm -v "<stack>:/stack:ro" ruby:3.3-alpine ruby /stack/build/selftest/post-restore-flow.rb /stack/Install/gitlab-post-restore.rb
# Без второго аргумента запускает все сценарии, каждый в отдельном процессе.
require 'net/http'
require 'json'
require 'base64'
require 'cgi'
require 'date'
require 'stringio'
require 'rbconfig'

SCENARIOS = %w[restore commit_fails clean clean_strict defaults_refused defaults_garbage].freeze
script = ARGV[0] or abort('usage: post-restore-flow.rb <gitlab-post-restore.rb> [scenario]')
unless ARGV[1]
  bad = SCENARIOS.reject do |s|
    puts "== #{s}"
    system(RbConfig.ruby, __FILE__, script, s)
  end
  puts bad.empty? ? 'ALL OK' : "FAILED: #{bad.join(', ')}"
  exit(bad.empty? ? 0 : 1)
end
scenario = ARGV[1]

OLD = '10.0.0.9'
NUGET = "\xEF\xBB\xBF<configuration>\r\n<add key=\"cicd\" value=\"http://10.0.0.9:8080/api/v4/projects/9/packages/nuget/index.json\" />\r\n<add key=\"n\" value=\"https://api.nuget.org/v3/index.json\" />\r\n</configuration>\r\n".b
KEYS = { 'DEPLOY_KEY_TEST' => 'testkey1234567890', 'DEPLOY_KEY_STAGE' => 'stagekey123456789', 'DEPLOY_KEY_PROD' => 'prodkey1234567890' }.freeze

def rule_json(name, push, merge)
  { 'name' => name, 'push_access_levels' => [{ 'access_level' => push }], 'merge_access_levels' => [{ 'access_level' => merge }], 'allow_force_push' => false }
end

$state = {
  groups: %w[client-modules reports], vars: { 'client-modules' => { 'DEPLOY_KEY' => 'old' } }, signup: true, commits: [], calls: [], fail_commits: scenario == 'commit_fails', refuse_defaults: scenario == 'defaults_refused', garbage_defaults: scenario == 'defaults_garbage',
  projects: {
    1 => { 'id' => 1, 'path_with_namespace' => 'client-modules/mod-a', 'archived' => false, 'branches' => %w[dev main release/1.0.0 feature/x],
           'rules' => [rule_json('main', 0, 40), rule_json('release/*', 0, 40)],
           'files' => { 'dev' => { 'Src/NuGet.Config' => NUGET.dup, '.gitlab-ci.yml' => %(script: curl -H "X-Deploy-Key: $PRJ_DEPLOY_KEY_TEST") },
                        'main' => { 'nuget.config' => NUGET.dup },
                        'release/1.0.0' => { 'nuget.config' => NUGET.dup }, 'feature/x' => { 'nuget.config' => NUGET.dup } },
           'vars' => ['DEPLOY_KEY_PROD'], 'ci_file' => true, 'ci_config_path' => '', 'gate' => false },
    2 => { 'id' => 2, 'path_with_namespace' => 'client-modules/old', 'archived' => true, 'branches' => [], 'rules' => [], 'files' => {}, 'vars' => [], 'gate' => false },
    3 => { 'id' => 3, 'path_with_namespace' => 'reports/r1', 'archived' => false, 'branches' => %w[master], 'rules' => [],
           'files' => { 'master' => { 'a/b/nuget.config' => NUGET.dup } }, 'vars' => [], 'ci_file' => false, 'ci_config_path' => 'ci/r.yml@pilot-ci/central', 'gate' => false },
    4 => { 'id' => 4, 'path_with_namespace' => 'client-modules/no-ci', 'archived' => false, 'branches' => %w[main], 'rules' => [],
           'files' => {}, 'vars' => [], 'ci_file' => false, 'ci_config_path' => '', 'gate' => false },
    6 => { 'id' => 6, 'path_with_namespace' => 'client-modules/current-ci', 'archived' => false, 'branches' => %w[dev], 'rules' => [],
           'files' => { 'dev' => { '.gitlab-ci.yml' => %(# DEPLOY_KEY_TEST (= MODULES_DEPLOY_KEY_TEST)\nDEPLOY_KEY="${DEPLOY_KEY_TEST:-}"\ncurl -H "X-Job-Token: $CI_JOB_TOKEN" -H "X-Deploy-Key: $DEPLOY_KEY" "$ADMIN_URL/api/ci/modules/deploy") } },
           'vars' => [], 'ci_file' => true, 'ci_config_path' => '', 'gate' => true },
    5 => { 'id' => 5, 'path_with_namespace' => 'client-modules/gated', 'archived' => false, 'branches' => [], 'rules' => [],
           'files' => {}, 'vars' => [], 'ci_file' => true, 'ci_config_path' => '', 'gate' => true }
  },
  group_defaults: {}
}
$state[:groups] = [] if scenario.start_with?('clean')

class FakeRes
  attr_reader :code, :body

  def initialize(code, body = '', headers = {})
    @code = code.to_s
    @body = body.is_a?(String) ? body : JSON.generate(body)
    @h = headers
  end

  def [](key)
    @h[key.downcase]
  end
end

class FakeHttp
  def request(req)
    verb = req.method
    body = req.body ? JSON.parse(req.body) : nil
    $state[:calls] << "#{verb} #{req.path}"
    raise 'no token' unless req['PRIVATE-TOKEN']

    path, query = req.path.split('?')
    query = CGI.parse(query.to_s)
    seg = path.sub('/api/v4/', '').split('/').map { |s| CGI.unescape(s.gsub('+', '%2B')) }
    return settings(verb, body) if seg == %w[application settings]
    return group(verb, seg, body) if seg[0] == 'groups' && seg.size == 2

    if seg[0] == 'groups' && seg[2] == 'projects'
      ps = $state[:projects].values.select { |x| x['path_with_namespace'].start_with?("#{seg[1]}/") }
      return FakeRes.new(200, ps.map { |x| x.slice('id', 'path_with_namespace', 'archived') }, 'x-next-page' => '')
    end
    return group_variables(verb, seg, body) if seg[0] == 'groups' && seg[2] == 'variables'

    project(verb, seg, query, body)
  end

  private

  def group(verb, seg, body)
    return FakeRes.new(404, '{}') unless $state[:groups].include?(seg[1])

    return FakeRes.new(403, 'blocked by instance settings') if verb == 'PUT' && $state[:refuse_defaults]
    return FakeRes.new(200, 'not json') if verb == 'GET' && $state[:garbage_defaults]

    $state[:group_defaults][seg[1]] = body['default_branch_protection_defaults'] if verb == 'PUT'
    d = $state[:group_defaults][seg[1]]
    FakeRes.new(200, d ? { 'default_branch_protection_defaults' => JSON.parse(JSON.generate(d)) } : {})
  end

  def settings(verb, body)
    $state[:signup] = body['signup_enabled'] if verb == 'PUT'
    FakeRes.new(200, { 'signup_enabled' => $state[:signup] })
  end

  def group_variables(verb, seg, body)
    g = ($state[:vars][seg[1]] ||= {})
    case verb
    when 'GET' then g.key?(seg[3]) ? FakeRes.new(200, { 'key' => seg[3] }) : FakeRes.new(404, '{}')
    when 'PUT' then g[seg[3]] = body and FakeRes.new(200, {})
    when 'POST' then g[body['key']] = body and FakeRes.new(201, {})
    else g.delete(seg[3]) ? FakeRes.new(204) : FakeRes.new(404, '{}')
    end
  end

  def project(verb, seg, query, body)
    pr = $state[:projects][seg[1].to_i] or raise "unknown project #{seg[1]}"
    if seg.size == 2
      pr['gate'] = body['only_allow_merge_if_pipeline_succeeds'] if verb == 'PUT'
      return FakeRes.new(200, { 'id' => pr['id'], 'default_branch' => pr['branches'].first || 'master', 'ci_config_path' => pr['ci_config_path'].to_s,
                                'only_allow_merge_if_pipeline_succeeds' => pr['gate'] })
    end
    case seg[2]
    when 'variables' then FakeRes.new(200, pr['vars'].map { |k| { 'key' => k } }, 'x-next-page' => '')
    when 'repository' then repository(pr, seg, query, body)
    when 'protected_branches' then protected_branches(pr, verb, seg, body)
    else raise "unhandled #{verb} #{seg.join('/')}"
    end
  end

  def repository(pr, seg, query, body)
    case seg[3]
    when 'branches' then FakeRes.new(200, pr['branches'].map { |b| { 'name' => b } }, 'x-next-page' => '')
    when 'tree' then FakeRes.new(200, (pr['files'][query['ref'][0]] || {}).keys.map { |f| { 'type' => 'blob', 'path' => f } }, 'x-next-page' => '')
    when 'files'
      return FakeRes.new(pr['ci_file'] ? 200 : 404, '{}') if seg[4] == '.gitlab-ci.yml' && seg[5].nil?

      content = (pr['files'][query['ref'][0]] || {})[seg[4]]
      content ? FakeRes.new(200, content) : FakeRes.new(404, '{"message":"404 File Not Found"}')
    when 'commits'
      br = body['branch']
      return FakeRes.new(500, 'boom') if $state[:fail_commits]
      return FakeRes.new(403, 'not allowed to push') if pr['rules'].any? { |r| File.fnmatch(r['name'], br) && r['push_access_levels'].any? { |l| l['access_level'].zero? } }

      body['actions'].each { |a| pr['files'][br][a['file_path']] = Base64.decode64(a['content']).b }
      $state[:calls] << "COMMIT #{pr['id']} #{br}"
      $state[:commits] << [pr['id'], br, body['commit_message']]
      FakeRes.new(201, {})
    else raise "unhandled repository #{seg.join('/')}"
    end
  end

  def protected_branches(pr, verb, seg, body)
    if seg[3].nil?
      return FakeRes.new(200, pr['rules'], 'x-next-page' => '') if verb == 'GET'

      pr['rules'] << rule_json(body['name'], body['push_access_level'], body['merge_access_level'])
      return FakeRes.new(201, {})
    end
    rule = pr['rules'].find { |x| x['name'] == seg[3] }
    return FakeRes.new(rule ? 200 : 404, rule || '{}') if verb == 'GET'

    pr['rules'].delete(rule)
    FakeRes.new(204)
  end
end

module Net
  class HTTP
    def self.start(*_args, **_opts)
      yield FakeHttp.new
    end
  end
end

class FakePat
  def revoke!
    $state[:revoked] = true
  end

  def set_token(_token); end
  def save!; end
end

class FakeUser
  def personal_access_tokens = self
  def where(**_opts) = []
  def build(_attrs) = FakePat.new
end

class User
  def self.find_by_username(_name) = FakeUser.new
end

class PersonalAccessToken
  def self.reflect_on_association(_name) = nil
end

module ActiveRecord
  class ActiveRecordError < StandardError; end
end

# OAuth-приложение админки с адресом возврата исходной машины.
module Doorkeeper
  class Application
    attr_accessor :redirect_uri, :name

    def self.find_by(uid:) = uid == 'cid' ? ($app ||= new) : nil

    def initialize
      @redirect_uri = 'http://localhost:5300/GitLab/Callback'
      @name = 'webAdmin'
    end

    def save! = ($state[:app_saved] = true)
  end
end

class Integer
  def day = self
  def from_now = Date.today + self
end

ENV.update(KEYS)
ENV.update('GROUP_URLS' => 'http://10.0.0.5:8080/client-modules,http://10.0.0.5:8080/client-modules/', 'REPORTS_GROUP_URL' => 'http://10.0.0.5:8080/reports',
           'OLD_HOST' => OLD, 'NEW_URL' => 'http://10.0.0.5:8080')
ENV.update('OAUTH_CLIENT_ID' => 'cid', 'ADMIN_CALLBACK' => 'http://10.0.0.5:5300/GitLab/Callback') if scenario == 'restore'
if scenario.start_with?('clean')
  ENV['OLD_HOST'] = ''
  ENV['ALLOW_MISSING_GROUPS'] = '1' if scenario == 'clean'
end

out = StringIO.new
real_stdout = $stdout
$stdout = out
status = nil
begin
  load script
rescue SystemExit => e
  status = e.status
ensure
  $stdout = real_stdout
end

fails = 0
check = lambda do |cond, msg|
  puts "  #{cond ? 'ok' : 'NOT OK'}: #{msg}"
  fails += 1 unless cond
end
calls = $state[:calls]
rules = ->(pid) { $state[:projects][pid]['rules'].to_h { |r| [r['name'], [r['push_access_levels'][0]['access_level'], r['merge_access_levels'][0]['access_level']]] } }
text = out.string
check.call(!KEYS.values.any? { |v| text.include?(v) } && !text.include?('glpat-'), 'output has no key values and no token')
check.call($state[:revoked] == true, 'temporary token revoked')
check.call($state[:signup] == false, 'signup closed')

case scenario
when 'restore'
  check.call(status == 0, "exit 0 (got #{status.inspect})")
  check.call($state[:commits].map { |c| c[0, 2] }.sort == [[1, 'dev'], [1, 'main'], [1, 'release/1.0.0'], [3, 'master']], 'commits: dev, main, release/*, reports master; not feature/x')
  check.call($state[:commits].all? { |c| c[2].include?('[skip ci]') }, 'commit message has [skip ci]')
  f = $state[:projects][1]['files']
  check.call(f['dev']['Src/NuGet.Config'] == NUGET.sub('10.0.0.9', '10.0.0.5') && f['feature/x']['nuget.config'] == NUGET, 'dev (nested, any case) fixed byte for byte; feature/x untouched')
  check.call($state[:projects][3]['files']['master']['a/b/nuget.config'] == NUGET.sub('10.0.0.9', '10.0.0.5'), 'reports group fixed')
  check.call(calls.none? { |c| c.include?('/projects/2/') }, 'archived project is not touched')
  check.call(rules.call(1) == { 'main' => [0, 40], 'release/*' => [40, 40], 'dev' => [40, 40] }, "protection: main push 0, release/* and dev push 40 (got #{rules.call(1)})")
  check.call(rules.call(3).empty?, 'reports group projects get no protection')
  ci_line = text.lines.find { |l| l.include?('old CI template') }.to_s
  check.call(ci_line.include?('client-modules/mod-a@dev') && !ci_line.include?('reports/'), 'old CI template in a module is reported, reports are not checked')
  check.call(!ci_line.include?('current-ci'), 'module with the current CI template is not reported')
  i = calls.index('COMMIT 1 main')
  check.call(i && calls[0...i].include?('DELETE /api/v4/projects/1/protected_branches/main') && calls[i..].include?('POST /api/v4/projects/1/protected_branches'),
             'No-one rule is removed before the commit and put back after it')
  v = $state[:vars]['client-modules']
  check.call(v.keys.sort == KEYS.keys.sort && v.values.all? { |x| x['masked'] } && !v['DEPLOY_KEY_TEST']['protected'] && v['DEPLOY_KEY_STAGE']['protected'] && v['DEPLOY_KEY_PROD']['protected'],
             'variables: TEST masked; STAGE, PROD masked+protected; old key removed')
  check.call(text.include?('mod-a overrides group variable DEPLOY_KEY_PROD'), 'project-level override is reported')
  check.call($state[:app_saved] && $app.redirect_uri == "http://localhost:5300/GitLab/Callback\nhttp://10.0.0.5:5300/GitLab/Callback",
             "oauth: admin callback added, the old one kept (got #{$app&.redirect_uri.inspect})")
  gate = ->(pid) { $state[:projects][pid]['gate'] }
  check.call(gate.call(1) == true && gate.call(3) == true && gate.call(5) == true, 'merge gate: module with CI file, report with custom ci_config_path, already-set one')
  check.call(gate.call(4) == false && gate.call(2) == false, 'merge gate: project without CI and archived project are not touched')
  check.call(calls.count('PUT /api/v4/projects/5') == 0 && calls.count('PUT /api/v4/projects/1') == 1, 'merge gate: no repeated PUT when already set')
  want = { 'allowed_to_push' => [{ access_level: 40 }], 'allowed_to_merge' => [{ access_level: 40 }], 'allow_force_push' => false, 'developer_can_initial_push' => true }
  gd = $state[:group_defaults]
  check.call(gd.keys.sort == %w[client-modules reports] && gd.values.all? { |d| JSON.parse(JSON.generate(d)) == JSON.parse(JSON.generate(want.transform_keys(&:to_s))) },
             'group defaults: modules and reports groups get push/merge Maintainers, developer initial push')
  check.call(calls.count { |c| c.start_with?('PUT /api/v4/groups/') } == 2, 'group defaults: one PUT per group, even though the module group is listed twice')
when 'commit_fails'
  check.call(status == 1, "exit 1 (got #{status.inspect})")
  check.call(rules.call(1) == { 'main' => [0, 40], 'release/*' => [40, 40], 'dev' => [40, 40] }, "protection is put back after a failed commit (got #{rules.call(1)})")
  check.call($state[:commits].empty?, 'no commit recorded')
  check.call(calls.count { |c| c.start_with?('PUT /api/v4/groups/') } == 2, 'group defaults are still set when the nuget commit fails')
when 'clean'
  check.call(status == 2, "exit 2: groups do not exist yet (got #{status.inspect})")
  check.call(!text.include?('ERROR') && $state[:commits].empty?, 'missing groups are a skip, not an error')
  check.call(text.include?('oauth: no client id or admin address, skipped') && !$state[:app_saved], 'oauth: skipped without client id and admin address')
when 'clean_strict'
  check.call(status == 1 && text.include?('not found'), "after a restore a missing group is an error (got #{status.inspect})")
when 'defaults_refused'
  check.call(status == 0, "exit 0: refused group defaults are a warning (got #{status.inspect})")
  check.call(text.include?('WARNING: group defaults') && !text.include?('ERROR'), 'warning is printed, no error')
  check.call($state[:projects][1]['gate'] == true, 'project steps still run')
when 'defaults_garbage'
  check.call(status == 1 && text.include?('ERROR') && text.include?('group defaults'), "unparsable group answer is an error, not a crash (got #{status.inspect})")
  check.call($state[:projects][1]['gate'] == true && $state[:projects][3]['gate'] == true, 'project steps still run')
end
exit(fails.zero? ? 0 : 1)
