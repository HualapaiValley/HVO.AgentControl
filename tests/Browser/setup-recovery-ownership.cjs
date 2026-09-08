function createFixture(docker, name) {
  const id = docker(['run', '-d', '--name', name, 'hvo-agentcontrol-ssh-fixture:local']).trim();
  if (!/^[a-f0-9]{64}$/i.test(id)) throw new Error('Docker did not return a fixture container ID.');
  return id;
}

function cleanupFixture(docker, id) {
  if (id) docker(['rm', '-f', id]);
}

module.exports = { createFixture, cleanupFixture };
