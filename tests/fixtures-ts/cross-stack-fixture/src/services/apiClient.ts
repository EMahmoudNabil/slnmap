import axios from 'axios';

const apiClient = axios.create({ baseURL: '/api/ogw' });

export default apiClient;
